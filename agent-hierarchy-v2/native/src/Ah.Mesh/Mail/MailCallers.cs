using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>
/// Who is asking: the working directory, the session id and process the caller knows of, its pane, and the team the caller pinned
/// (<paramref name="TeamArg"/>, or <paramref name="TeamDefaultExplicit"/> for the legacy default team named on purpose).
/// <paramref name="HerdrFlavour"/> is the herdr server the caller's pane lives on, read from the same env as its pane; null when unknown.
/// </summary>
public sealed record CallerRequest(
    string Cwd,
    string? SessionId = null,
    int? Pid = null,
    string? PaneId = null,
    string? TeamArg = null,
    bool TeamDefaultExplicit = false,
    string? HerdrFlavour = null);

/// <summary>
/// One of a caller's boxes. A box derived from the team files carries its team, member name and team file; one read from a
/// registration carries only the address and directory.
/// </summary>
public sealed record CallerBox(string Address, string Dir, string? Team = null, string? Name = null, string? TeamFile = null);

/// <summary>An Orchestrator box of a team whose live Orchestrator is this process or one of its ancestors, and the session that holds it.</summary>
public sealed record DeciderBox(string Address, string Team, string? Session)
{
    public string? Dir { get; init; }
}

/// <summary>Which stale half of an Orchestrator record a rebind may repair.</summary>
[Flags]
public enum RebindModes
{
    None = 0,

    /// <summary>The recorded pid is this process but another session id: record this session (after <c>/clear</c> or compaction).</summary>
    Session = 1,

    /// <summary>The recorded session is this one but its pid is gone: record this process (after <c>--resume</c> or <c>--continue</c>).</summary>
    Pid = 2,

    Both = Session | Pid,
}

/// <summary>
/// A claim that session <paramref name="SessionId"/> runs as process <paramref name="Pid"/>, already proven process-tied by the
/// caller, and which repairs it may make. <paramref name="Hook"/> is true when the write is made from a hook.
/// </summary>
public sealed record RebindRequest(string Cwd, string SessionId, int Pid, RebindModes Modes, bool Hook = false);

/// <summary>One team record a rebind changed: <paramref name="Field"/> is <c>session_id</c> or <c>pid</c>.</summary>
public sealed record RebindChange(string Team, string TeamFile, string Field);

/// <summary>What a rebind did, and what it declined or failed to do (<paramref name="Notes"/>, for the caller to log).</summary>
public sealed record RebindResult(IReadOnlyList<RebindChange> Changed, IReadOnlyList<string> Notes);

/// <summary>
/// The client side of the mailbox: everything derived from the caller's own environment. <see cref="RequestContext"/> resolves the
/// caller-derived inputs once; the CLI and the hooks pass the result to the mailbox explicitly. The identity functions (who is this
/// session) run only on the client, whose environment and process tree are the caller's.
/// </summary>
public interface IMailCallers
{
    /// <summary>
    /// The caller-derived context of a request: <paramref name="cwd"/>, its hierarchy directory (honouring <c>WRANGL_HIERARCHY_DIR</c>),
    /// the main checkout's (empty when none) and the directory of <c>CLAUDE_CODE_MESSAGING_SOCKET</c> (empty when unset).
    /// </summary>
    RequestContext RequestContext(string cwd);

    /// <summary>
    /// Who this caller is, as boxes (empty: not a team member or Orchestrator). An Orchestrator has one address per named team it
    /// owns; otherwise the session is a member whose pane holds a member row. The legacy default <c>team.json</c> is not addressable.
    /// </summary>
    IReadOnlyList<CallerBox> ResolveCaller(CallerRequest request);

    /// <summary>Resolves the caller and records the session's registration. Returns the caller's boxes (empty: nothing was written).</summary>
    IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request);

    /// <summary>
    /// Registers the caller again when the session already holds a registration that names another process, as after the session was
    /// resumed under a new pid. A session with no registration is left alone. The same rules as <see cref="RegisterCaller"/> apply, so a
    /// registration whose process is still alive is not displaced, and a caller whose pane no member row names yet writes nothing and
    /// is retried by the next call. Returns whether the registration now names the caller's process.
    /// </summary>
    bool RebindStaleRegistration(CallerRequest request) => false;

    /// <summary>
    /// Registers the caller again unless its session's registration names this process and was written by this function at the
    /// teams' present stamp; then nothing is resolved or written. The prompt self-heal for a session whose SessionStart never
    /// registered it, or whose boxes drifted (b340). Returns the caller's boxes.
    /// </summary>
    IReadOnlyList<CallerBox> EnsureRegistered(CallerRequest request) => [];

    /// <summary>The caller's boxes: its live registration (an Orchestrator's is re-derived, since it may have gained teams), else resolved and registered.</summary>
    IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request);

    /// <summary>
    /// The Orchestrator boxes of the caller deciding a join, derived from the process tree and the registrations and from nothing the
    /// caller passes: the teams whose recorded Orchestrator pid is alive, is this process or one of its ancestors, and is the pid of a
    /// registration holding <c>orchestrator@&lt;team&gt;</c>. Nothing is registered.
    /// </summary>
    IReadOnlyList<DeciderBox> JoinDeciderBoxes(string cwd, string? sessionId = null);

    IReadOnlyList<DeciderBox> SessionDeciderBoxes(string cwd, string? sessionId) => JoinDeciderBoxes(cwd, sessionId);

    /// <summary>
    /// Acks <paramref name="ids"/> in a box for a hook or a verb. A freshly acked <c>relay-needed</c> request makes its receiving
    /// session owe the relay (see <see cref="IRelayObligations"/>); a failure to write that obligation does not undo the ack.
    /// </summary>
    AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null);

    /// <summary>The command line of a live process as the system <c>ps</c> printed it, or null when <c>ps</c> cannot answer.</summary>
    string? ProcessCommand(int pid);

    /// <summary>
    /// Repairs the Orchestrator record of every named team the session's candidate hierarchy dirs hold, for a (session, process) pair
    /// the caller has proven. <see cref="RebindModes.Session"/> sets <c>session_id</c> on a team whose recorded pid is the process
    /// and which was created at or after that process started. <see cref="RebindModes.Pid"/> sets <c>pid</c> (and <c>pid_start</c>)
    /// on a team that names the session while its recorded process is gone or has another start time. The test is repeated under
    /// the team file's lock. In test mode a team file outside an <c>ahprobe.*</c> sandbox is never written.
    /// </summary>
    RebindResult RebindOwnedTeams(RebindRequest request) => new([], []);

    /// <summary>
    /// Pins the start time of each team Orchestrator record that has a pid but no start: from its session's live registration, and
    /// only when that registration's pid is the recorded one. Teams are looked for in the registration's own box directories.
    /// Returns how many records were written.
    /// </summary>
    int PinOrchestratorStarts() => 0;

    /// <summary>
    /// Whether the process a team's Orchestrator record names is gone: <paramref name="pid"/> is not alive, or it is alive with
    /// a start time other than <paramref name="recordedStart"/> (the pid was reused). A start time that cannot be read counts as alive.
    /// </summary>
    bool OrchestratorProcessIsGone(double pid, string? recordedStart) => false;
}

internal sealed class MailCallers(
    Mailbox mailbox,
    IConfigCore config,
    IRosterCore roster,
    HierarchyServices hierarchy,
    IRelayObligations relay,
    IAhEnvironment environment,
    IProcessTable processes,
    string? ahVersion = null,
    IClock? clock = null) : IMailCallers
{
    private readonly RegistrationBinder _binder = new(pid => hierarchy.Core.PidAlive(JsonValue.FromNumber(pid)), processes);

    public RequestContext RequestContext(string cwd)
    {
        var socket = environment.GetVariable("CLAUDE_CODE_MESSAGING_SOCKET");
        return new RequestContext(
            cwd,
            config.HierarchyDir(cwd),
            config.MainHierarchyDir(cwd) ?? string.Empty,
            string.IsNullOrEmpty(socket) ? string.Empty : NodePath.Dirname(socket));
    }

    public IReadOnlyList<CallerBox> ResolveCaller(CallerRequest r)
    {
        var dir = config.HierarchyDir(r.Cwd);
        var main = config.MainHierarchyDir(r.Cwd);
        var homes = new List<string> { dir };
        if (!string.IsNullOrEmpty(main))
        {
            homes.Add(main);
        }

        var livePid = r.Pid is { } p && hierarchy.Core.PidAlive(JsonValue.FromNumber(p)) ? p : (int?)null;
        var owned = mailbox.OwnedTeamsAnywhere(RequestContext(r.Cwd), new TeamInvoker(livePid, string.IsNullOrEmpty(r.SessionId) ? null : r.SessionId));
        var member = MemberBoxOf(r, dir, homes);
        if (owned.Count == 0)
        {
            return member is null ? [] : [member];
        }

        var boxes = owned.Select(t => ChiefWhileDeputyOnDuty(t, member, r.SessionId)
            ? new CallerBox(HierarchyModule.ChiefAddress(t.Team), mailbox.BoxDirFor(t.TeamFile, "chief"), t.Team, "chief", t.TeamFile)
            : new CallerBox(HierarchyModule.OrchestratorAddress(t.Team), mailbox.BoxDirFor(t.TeamFile, "orchestrator"), t.Team, "orchestrator", t.TeamFile)).ToList();
        boxes.AddRange(owned.Select(ChiefLeftover).OfType<CallerBox>().Where(c => boxes.All(b => b.Address != c.Address)).ToList());
        if (member is not null && boxes.All(b => b.Address != member.Address))
        {
            boxes.Add(member);
        }

        return boxes;
    }

    /// <summary>
    /// Whether the caller owns <paramref name="owned"/> as its chief while a live deputy is on duty there: the team's <c>duty.deputy</c>
    /// row is live and the caller is not that row. The chief then holds <c>chief@T</c> and the deputy <c>orchestrator@T</c>; once the
    /// deputy is dead the rule lapses and the chief is <c>orchestrator@T</c> again, with nothing written.
    /// </summary>
    /// <summary>
    /// <c>chief@T</c> while it still holds unacked mail and no live deputy is on duty in T: mail sent there after <c>duty off</c>, or
    /// left when the deputy died, stays the owner's to drain and be rung for. A team whose chief box is empty or absent adds nothing.
    /// </summary>
    private CallerBox? ChiefLeftover(OwnedTeam owned)
    {
        var dir = mailbox.BoxDirFor(owned.TeamFile, "chief");
        var acked = mailbox.AckedIn(dir);
        var open = mailbox.ScanEntries(dir).Rows.Any(r => r.Entry.Member("id") is { IsText: true } id && !acked(id.AsString()));
        return open && !mailbox.DeputyOnDutyLive(owned.TeamFile, owned.Team)
            ? new CallerBox(HierarchyModule.ChiefAddress(owned.Team), dir, owned.Team, "chief", owned.TeamFile)
            : null;
    }

    private bool ChiefWhileDeputyOnDuty(OwnedTeam owned, CallerBox? member, string? sessionId)
    {
        if (roster.ReadTeamFileAt(owned.TeamFile).Member("duty")?.Member("deputy") is not { IsText: true } named || named.AsString().Length == 0
            || hierarchy.Core.Liveness?.ForMember(named.AsString(), owned.Team) is not { Live: true } deputy)
        {
            return false;
        }

        var isDeputy = member?.Address == $"{named.AsString()}@{owned.Team}" || (!string.IsNullOrEmpty(sessionId) && deputy.SessionId == sessionId);
        return !isDeputy;
    }

    /// <summary>The box of the member row the caller's pane belongs to, or null when no member row answers for it.</summary>
    private CallerBox? MemberBoxOf(CallerRequest r, string dir, List<string> homes)
    {
        var marked = environment.GetVariable(MeshModule.MemberMarkerEnv) is { Length: > 0 } markerName
            ? new MemberMarker(
                markerName,
                r.Pid ?? 0,
                r.Pid is { } own ? processes.StartTime(own) : null,
                clock?.UtcNow ?? TimeProvider.System.GetUtcNow(),
                (team, member) => hierarchy.Core.Liveness?.ForMember(member, team) is { Live: true } held && !(r.SessionId is { Length: > 0 } own && held.SessionId == own))
            : null;
        var found = roster.LocateSession(dir, homes, r.TeamArg, r.TeamDefaultExplicit, r.PaneId, marked);
        var teamName = found.Get("teamName") is { IsText: true } tn && tn.AsString().Length > 0 ? tn.AsString() : null;
        var member = found.Get("member") is { IsMap: true } m ? m.AsObject() : null;
        var memberName = member?.Get("name") is { IsText: true } mn && mn.AsString().Length > 0 ? mn.AsString() : null;
        if (!(found.Get("answeredBy") is { IsTruthy: true }) || teamName is null || memberName is null)
        {
            return null;
        }

        var home = found.Get("home") is { IsText: true } h ? h.AsString() : dir;
        var file = roster.TeamPath(home, teamName);
        return new CallerBox($"{memberName}@{teamName}", mailbox.BoxDirFor(file, memberName), teamName, memberName, file);
    }

    public IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request) => Register(request, ResolveCaller(request));

    public IReadOnlyList<CallerBox> EnsureRegistered(CallerRequest request)
    {
        if (string.IsNullOrEmpty(request.SessionId))
        {
            return [];
        }

        // The common case costs a registration read and the teams stamp: this process's registration, written at the teams' present state.
        var held = mailbox.ReadRegistration(request.SessionId);
        var stamp = mailbox.TeamsStamp(RequestContext(request.Cwd));
        if (held is not null && stamp is not null && held.Get("teams_stamp") is { IsText: true } heldStamp && heldStamp.AsString() == stamp
            && (request.Pid is not { } pid || held.Get("pid") is { IsNumber: true } p && p.AsNumber() == pid))
        {
            return [.. mailbox.RegistrationBoxes(held).Select(static b => new CallerBox(b.Address, b.Dir))];
        }

        return Register(request, ResolveCaller(request), stamp);
    }

    private IReadOnlyList<CallerBox> Register(CallerRequest request, IReadOnlyList<CallerBox> boxes, string? teamsStamp = null)
    {
        if (boxes.Count > 0 && !string.IsNullOrEmpty(request.SessionId))
        {
            var pidStart = request.Pid is { } own ? processes.StartTime(own) : null;
            var bound = _binder.Binds(request.Pid, pidStart, System.Environment.ProcessId);
            var write = new RegistrationWrite(
                request.SessionId,
                boxes.Select(b => new BoxRef(b.Address, b.Dir)).ToList(),
                TeamFile: boxes[0].TeamFile,
                Harness: mailbox.ReadRegistration(request.SessionId)?.Get("harness") is { IsText: true } held ? held.AsString() : "claude",
                TeamFiles: boxes.Where(b => b.TeamFile is not null).ToDictionary(b => b.Address, b => b.TeamFile!),
                Pid: request.Pid,
                PaneId: request.PaneId,
                Cwd: request.Cwd,
                PidStart: pidStart,
                Bound: bound,
                AhVersion: ahVersion,
                HerdrFlavour: request.HerdrFlavour,
                TeamsStamp: teamsStamp);

            // a claim never rewrites the pid and pane of a registration it must not displace; it still resolves boxes
            _ = mailbox.RegisterGuarded(write, HeldStart(request.SessionId));
        }

        return boxes;
    }

    public bool RebindStaleRegistration(CallerRequest request)
    {
        if (string.IsNullOrEmpty(request.SessionId) || request.Pid is not { } pid
            || mailbox.ReadRegistration(request.SessionId)?.Get("pid") is not { IsNumber: true } held
            || held.AsNumber() == pid)
        {
            return false;
        }

        RegisterCaller(request);
        return mailbox.ReadRegistration(request.SessionId)?.Get("pid") is { IsNumber: true } now && now.AsNumber() == pid;
    }

    /// <summary>
    /// The held registration's process as it is now: alive or not, and its start time when the registration recorded one (a pid
    /// that has another start time was reused, so the registration no longer names a live process).
    /// </summary>
    private RegistrationHeld? HeldStart(string sessionId)
    {
        var existing = mailbox.ReadRegistration(sessionId);
        if (existing?.Get("pid") is not { IsNumber: true } held || !double.IsInteger(held.AsNumber()) || held.AsNumber() <= 1
            || held.AsNumber() > int.MaxValue)
        {
            return null;
        }

        var pid = (long)held.AsNumber();
        var recorded = existing.Get("pid_start") is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : null;
        return new RegistrationHeld(
            pid,
            hierarchy.Core.PidAlive(held),
            recorded is null ? null : ProcessStart.CurrentAs(processes, pid, recorded));
    }

    public IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request)
    {
        var reg = string.IsNullOrEmpty(request.SessionId) ? null : mailbox.ReadRegistration(request.SessionId);
        if (reg is not null
            && hierarchy.Core.PidAlive(reg.Get("pid") ?? JsonValue.Null)
            && !JsConvert.ToJsString(reg.Get("address")).StartsWith("orchestrator@", StringComparison.Ordinal))
        {
            var boxes = mailbox.RegistrationBoxes(reg);
            if (boxes.Count > 0)
            {
                return boxes.Select(b => new CallerBox(b.Address, b.Dir)).ToList();
            }
        }

        // b356: the stamp fast path skips the write while the teams are unchanged; a stamp change still re-derives and re-registers
        return string.IsNullOrEmpty(request.SessionId) ? RegisterCaller(request) : EnsureRegistered(request);
    }

    public string? ProcessCommand(int pid) => processes.Command(pid);

    public IReadOnlyList<DeciderBox> JoinDeciderBoxes(string cwd, string? sessionId = null)
    {
        var dir = config.HierarchyDir(cwd);
        var line = new HashSet<int>(processes.Ancestors(System.Environment.ProcessId) ?? []) { System.Environment.ProcessId };
        var registrations = mailbox.AllRegistrations();
        return DeciderBoxesIn(dir, sessionId, line, registrations);
    }

    public IReadOnlyList<DeciderBox> SessionDeciderBoxes(string cwd, string? sessionId)
    {
        var line = new HashSet<int>(processes.Ancestors(System.Environment.ProcessId) ?? []) { System.Environment.ProcessId };
        var registrations = mailbox.AllRegistrations();
        var cwds = new List<string> { cwd };
        foreach (var registration in registrations)
        {
            if (registration.Get("pid") is not { IsNumber: true } number
                || !line.Any(pid => pid == number.AsNumber())
                || registration.Get("bound") is not { Kind: JsonKind.Bool, IsTruthy: true }
                || !SameProcess(registration, (long)number.AsNumber())
                || registration.Get("cwd") is not { IsText: true } registeredCwd || registeredCwd.AsString().Length == 0)
            {
                continue;
            }

            cwds.Add(registeredCwd.AsString());
        }

        var found = DistinctRealDirs(cwds).SelectMany(d => DeciderBoxesIn(d, sessionId, line, registrations)).ToList();

        // A team can live in another repository than the session's cwd. The cwds of the registered sessions lead to it, and the
        // directory of the box this session holds (named by a hash of the team file's path) proves which team file is its own.
        var registeredCwds = registrations.Select(r => r.Get("cwd")).OfType<JsonValue>()
            .Where(v => v.IsText && v.AsString().Length > 0).Select(v => v.AsString());
        foreach (var dir in DistinctRealDirs(cwds.Concat(registeredCwds)))
        {
            found.AddRange(DeciderBoxesIn(dir, sessionId, line, registrations, byHeldBox: true));
        }

        return found.DistinctBy(b => (b.Dir, b.Address)).ToList();
    }

    private IEnumerable<string> DistinctRealDirs(IEnumerable<string> cwds) =>
        cwds.SelectMany(c => new[] { config.HierarchyDir(c), config.MainHierarchyDir(c) })
            .OfType<string>().Where(d => d.Length > 0)
            .Select(d => mailbox.RealPath(d) is { Length: > 0 } real ? real : d)
            .Distinct(StringComparer.Ordinal);

    // With byHeldBox the holding registration is matched by the box directory this team file maps to, and a bound registration with
    // no recorded start time is accepted when the team file's orchestrator has the same pid and session and its own recorded start
    // time, if any, is the live one.
    private List<DeciderBox> DeciderBoxesIn(string dir, string? sessionId, HashSet<int> line, IReadOnlyList<JsonObject> registrations, bool byHeldBox = false)
    {
        var found = new List<DeciderBox>();
        foreach (var team in roster.ListTeamNames(dir))
        {
            var record = roster.ReadTeamFileAt(roster.TeamPath(dir, team));
            var orchestrator = record is { IsMap: true } v && v.AsObject().Get("orchestrator") is { IsMap: true } o ? o.AsObject() : null;
            var pidValue = orchestrator?.Get("pid");
            var pid = pidValue is { IsNumber: true } n ? n.AsNumber() : JsConvert.ToNumber(pidValue);
            if (!(double.IsFinite(pid) && pid == Math.Floor(pid) && pid > 1 && pid <= int.MaxValue)
                || !hierarchy.Core.PidAlive(JsonValue.FromNumber(pid))
                || !line.Contains((int)pid))
            {
                continue;
            }

            var recorded = orchestrator?.Get("session_id") is { IsTruthy: true } sid ? JsConvert.ToJsString(sid) : null;
            if (recorded is not null && !string.IsNullOrEmpty(sessionId) && recorded != sessionId)
            {
                continue;
            }

            var address = HierarchyModule.OrchestratorAddress(team);
            var boxDir = byHeldBox ? mailbox.BoxDirFor(roster.TeamPath(dir, team), "orchestrator") : null;
            var teamStart = orchestrator?.Get("pid_start") is { IsText: true } ts ? ts.AsString() : string.Empty;
            var held = registrations.FirstOrDefault(r =>
                JsConvert.ToNumber(r.Get("pid")) == pid
                && mailbox.RegistrationBoxes(r).Any(b => b.Address == address && (boxDir is null || b.Dir == boxDir))
                && (SameProcess(r, (long)pid)
                    || (byHeldBox
                        && r.Get("bound") is { Kind: JsonKind.Bool, IsTruthy: true }
                        && r.Get("pid_start") is not { IsText: true }
                        && !string.IsNullOrEmpty(sessionId) && recorded == sessionId
                        && (teamStart.Length == 0 || ProcessIdentity.Same(processes, (long)pid, teamStart)))));
            if (held is null)
            {
                continue;
            }

            var heldSession = held.Get("session_id") is { IsTruthy: true } hs ? JsConvert.ToJsString(hs) : null;
            if (!string.IsNullOrEmpty(sessionId) && heldSession is not null && heldSession != sessionId)
            {
                continue;
            }

            found.Add(new DeciderBox(address, team, heldSession) { Dir = dir });
        }

        return found;
    }

    /// <summary>
    /// Whether the process now holding the registered pid is the one the registration was written for: its recorded start time
    /// equals the current one. A registration with no recorded start time, or a pid <c>ps</c> cannot answer for, is not the same.
    /// </summary>
    private bool SameProcess(JsonObject registration, long pid) =>
        registration.Get("pid_start") is { IsText: true } recorded
        && ProcessIdentity.Same(processes, pid, recorded.AsString());

    public int PinOrchestratorStarts()
    {
        var pinned = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in mailbox.LiveRegistrations())
        {
            if (registration.Get("session_id") is not { IsText: true } sid || sid.AsString().Length == 0
                || registration.Get("pid") is not { IsNumber: true } p || !double.IsInteger(p.AsNumber()) || p.AsNumber() <= 1
                || registration.Get("pid_start") is not { IsText: true } st || st.AsString().Length == 0)
            {
                continue;
            }

            var session = sid.AsString();
            var pid = p.AsNumber();
            var start = st.AsString();

            JsonObject? Decide(JsonValue doc)
            {
                if (!doc.IsMap || doc.AsObject().Get("orchestrator") is not { IsMap: true } held)
                {
                    return null;
                }

                var orchestrator = held.AsObject();
                if (orchestrator.Get("pid_start") is { IsText: true } recorded && recorded.AsString().Length > 0
                    || orchestrator.Get("session_id") is not { IsText: true } s || s.AsString() != session
                    || orchestrator.Get("pid") is not { IsNumber: true } n || n.AsNumber() != pid)
                {
                    return null;
                }

                var next = orchestrator.Clone();
                next["pid_start"] = start;
                return next;
            }

            foreach (var path in mailbox.RegistrationTeamFiles(registration).Distinct(StringComparer.Ordinal))
            {
                var dir = Ah.Roster.RosterModule.TeamFileHome(path) ?? NodePath.Dirname(path);
                var key = mailbox.RealPath(path);
                try
                {
                    if (!seen.Add(string.IsNullOrEmpty(key) ? path : key) || Decide(roster.ReadTeamFileAt(path)) is null
                        || environment.TestMode && !RealPathResolver.IsUnderProbeSandbox(string.IsNullOrEmpty(key) ? path : key))
                    {
                        continue;
                    }

                    var wrote = false;
                    hierarchy.StateCalls.UpdateTeamFile(
                        path,
                        dir,
                        doc =>
                        {
                            if (Decide(doc) is not { } next)
                            {
                                return null;
                            }

                            var copy = doc.AsObject().Clone();
                            copy["orchestrator"] = next;
                            wrote = true;
                            return TeamFileOutcome.Write(copy);
                        });
                    pinned += wrote ? 1 : 0;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // one unreadable or contended team file does not stop the others; the next daemon start retries it
                }
            }
        }

        return pinned;
    }

    public RebindResult RebindOwnedTeams(RebindRequest r)
    {
        var changed = new List<RebindChange>();
        var notes = new List<string>();
        if (r.Pid <= 1 || string.IsNullOrEmpty(r.SessionId) || r.Modes == RebindModes.None)
        {
            return new RebindResult(changed, notes);
        }

        var pidStart = processes.StartTime(r.Pid);
        var haveStart = pidStart is not null && Timestamps.TryParseProcessStart(pidStart, TimeZoneInfo.Utc, out _);
        var startSeconds = haveStart && Timestamps.TryParseProcessStart(pidStart, TimeZoneInfo.Utc, out var ms) ? Math.Floor(ms / 1000) : double.NaN;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        (JsonObject? Next, string? Field) Decide(JsonValue doc)
        {
            if (!doc.IsMap || doc.AsObject().Get("orchestrator") is not { IsMap: true } held)
            {
                return (null, null);
            }

            var orchestrator = held.AsObject();
            var pid = orchestrator.Get("pid") is { IsNumber: true } n && double.IsInteger(n.AsNumber()) ? n.AsNumber() : double.NaN;
            var session = orchestrator.Get("session_id") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
            string? field = null;
            var next = orchestrator.Clone();
            if (r.Modes.HasFlag(RebindModes.Session) && pid == r.Pid && session != r.SessionId
                && !double.IsNaN(startSeconds)
                && doc.AsObject().Get("created") is { IsText: true } created
                && Timestamps.TryParseMs(created.AsString(), out var createdMs)
                && Math.Floor(createdMs / 1000) >= startSeconds)
            {
                next["session_id"] = r.SessionId;
                field = "session_id";
            }
            else if (r.Modes.HasFlag(RebindModes.Pid) && session == r.SessionId && pid != r.Pid && RecordedProcessIsGone(orchestrator, pid))
            {
                next["pid"] = (double)r.Pid;
                if (pidStart is not null)
                {
                    next["pid_start"] = pidStart;
                }
                else
                {
                    next.Remove("pid_start");
                }

                field = "pid";
            }

            return field is null ? (null, null) : (next, field);
        }

        foreach (var dir in mailbox.OwnerCandidateDirs(RequestContext(r.Cwd), r.SessionId))
        {
            foreach (var name in roster.ListTeamNames(dir))
            {
                var path = roster.TeamPath(dir, name);
                var key = mailbox.RealPath(path);
                if (!seen.Add(string.IsNullOrEmpty(key) ? path : key))
                {
                    continue;
                }

                try
                {
                    if (Decide(roster.ReadTeamFileAt(path)).Field is null)
                    {
                        continue;
                    }

                    if (environment.TestMode && !RealPathResolver.IsUnderProbeSandbox(string.IsNullOrEmpty(key) ? path : key))
                    {
                        notes.Add($"rebind skipped: test mode never writes team file {path}, which is outside an ahprobe sandbox");
                        continue;
                    }

                    string? done = null;
                    hierarchy.StateCalls.UpdateTeamFile(
                        path,
                        dir,
                        doc =>
                        {
                            var (next, field) = Decide(doc);
                            if (next is null)
                            {
                                done = null;
                                return null;
                            }

                            var copy = doc.AsObject().Clone();
                            copy["orchestrator"] = next;
                            done = field;
                            return TeamFileOutcome.Write(copy);
                        },
                        new StateCallOptions(Hook: r.Hook));
                    if (done is not null)
                    {
                        changed.Add(new RebindChange(name, path, done));
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    notes.Add($"rebind failed for team file {path}: {e.Message}");
                }
            }
        }

        return new RebindResult(changed, notes);
    }

    /// <summary>
    /// Whether the process a team's Orchestrator record names is gone: its pid is not alive, or it is alive with a start time other
    /// than the one recorded (the pid was reused). A recorded process whose start time cannot be read counts as alive.
    /// </summary>
    private bool RecordedProcessIsGone(JsonObject orchestrator, double pid) =>
        OrchestratorProcessIsGone(pid, orchestrator.Get("pid_start") is { IsText: true } recorded ? recorded.AsString() : null);

    public bool OrchestratorProcessIsGone(double pid, string? recordedStart)
    {
        if (!(double.IsFinite(pid) && pid > 1 && pid <= int.MaxValue))
        {
            return true;
        }

        if (!hierarchy.Core.PidAlive(JsonValue.FromNumber(pid)))
        {
            return true;
        }

        return ProcessIdentity.Reused(processes, (long)pid, recordedStart);
    }

    public AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null)
    {
        var result = mailbox.AckIds(dir, ids, by, sessionId);
        try
        {
            relay.ObligationsFor(sessionId, [dir], result.Fresh, by);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Ah.Core.Json.StoreUnavailableException)
        {
            // the ack stands; an obligation that could not be written, or whose dedupe read the store could not answer (logged where the
            // read failed), is covered by the rung-3 deadline
        }

        return result;
    }

}
