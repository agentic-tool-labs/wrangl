using Ah.Abstractions;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// The services the roster verbs run on, handed to <see cref="CommandsModule"/> without building anything: the service graph is
/// built the first time a roster verb asks for them.
/// </summary>
internal sealed class RosterComposition(ServiceGraph graph, IClock clock, IAhEnvironment environment) : IRosterCliServices
{
    // files come only from the graph's reader, the one that reads the asks logs from the state database (per-dir-files-to-db §6c G5)
    private readonly IFileReader _files = graph.Io.Files;

    internal static void Compose(Lazy<ServiceGraph> graph, IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(environment);
        var services = new Lazy<IRosterCliServices>(
            () => new RosterComposition(graph.Value, graph.Value.Io.Clock, environment),
            LazyThreadSafetyMode.ExecutionAndPublication);
        CommandsModule.ComposeRoster(() => services.Value);
    }

    public IConfigCore Config => graph.Config;

    public IDaemonClient? Daemon => graph.Daemon;

    public IRosterCore Roster => graph.Roster;

    public HierarchyServices Hierarchy => graph.Hierarchy;

    public string? CloseShippedBacklog(string pool, string id, string sha) => Ah.Cli.Verbs.BacklogVerb.CloseShipped(graph, environment.AhHome, pool, id, sha);

    public string? CloseDoneBacklog(string pool, string id, IReadOnlyList<string> commits, string note) => Ah.Cli.Verbs.BacklogVerb.CloseDone(graph, environment.AhHome, pool, id, commits, note, subItemsHold: true);

    public string? WorkAlias(string pool, string team, string id) =>
        Ah.Cli.Verbs.WorkVerb.Resolve(graph, environment.AhHome, pool, id, team).Row is { IsBacklog: false } row && row.Team == team ? row.Stream + "/" + row.Item : null;

    public string? WorkParent(string pool, string team, string id) =>
        Ah.Cli.Verbs.WorkVerb.Resolve(graph, environment.AhHome, pool, id, team).Row is { IsBacklog: true } row ? row.Item : null;

    public Ah.Core.Json.JsonObject? TeamGoal(string cwd, string team) =>
        RulesReads.ShownGoal(environment.AhHome, () => Ah.Cli.Verbs.BacklogVerb.DirOf(graph, cwd), dir => RulesReads.TeamChain(graph, dir, team), graph.OpenReader);

    public IAhEnvironment Environment => environment;

    public IClock Clock => clock;

    public IFileReader Files => _files;

    public IGrantSource? Grants => graph.Grants;

    public IHomeDocs? HomeDocs => graph.HomeDocs;
    public IRecoverRecordStore? RecoverRecords => graph.RecoverRecords;

    public string CurrentDirectory => graph.Facts.CurrentDirectory;

    public int ParentPid => graph.Facts.ParentPid;

    /// <summary>The launch layer's teardown reads and closes, built the first time a verb asks.</summary>
    public ITeardown? Teardown => graph.Launch.Teardown;

    /// <summary>The launch layer (launcher, shapes, layout, mux, member rules), built the first time a verb asks.</summary>
    public IRosterLaunch? Launch => graph.Launch;

    private IRosterPanes? _panes;
    private IRosterDoctor? _doctor;

    /// <summary>The reads <c>roster doctor</c> makes beyond the libraries.</summary>
    public IRosterDoctor? Doctor => _doctor ??= new DoctorComposition(graph, environment);

    private IRosterCodexAnswers? _codexAnswers;

    public IRosterCodexAnswers? CodexAnswers => _codexAnswers ??= new CodexAnswersComposition(graph);

    private IRosterBareRecover? _bareRecover;

    public IRosterBareRecover? BareRecover => _bareRecover ??= new BareRecoverComposition(graph);

    private IRosterSpawnAdmission? _spawnAdmission;

    public IRosterSpawnAdmission? SpawnAdmission => _spawnAdmission ??= new SpawnAdmissionComposition(graph);

    /// <summary>The pane services (paste, screens, mesh gate), built the first time a verb asks.</summary>
    public IRosterPanes? Panes => _panes ??= new PaneComposition(graph, environment, clock);

    /// <summary>A test's spawn starts nothing detached: it would outlive the test.</summary>
    public bool StartDetached(string commandLine, string cwd, string log)
    {
        if (environment.TestMode)
        {
            return false;
        }

        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = entry.Value as string;
        }

        _ = PlatformModule.CreateDetachedSpawner().SpawnDetached(new DetachedSpawnRequest(["/bin/sh", "-c", commandLine], cwd, variables, log));
        return true;
    }

    /// <summary>The mailbox send behind roster verbs that announce an event.</summary>
    public IRosterEventMail? EventMail => new EventMailComposition(graph.Mailbox);

    /// <summary>The mesh client's caller registration: resolves who the session is and records its registration.</summary>
    public IRosterCallerRegistry? Callers => new CallerRegistry(graph.Callers, graph.Mailbox, PlatformModule.CreateProcessTable(graph.Runner));

    private IRosterScratch? _scratch;

    /// <summary>The member tmp operations over the real file system, and git's answer on whether a path is ignored.</summary>
    public IRosterScratch? Scratch => _scratch ??= new GitScratch(PlatformModule.CreateScratch(_files, environment.AhHome), graph.Runner);

    /// <summary>The hook-error log, for the verbs that leave a row where a hook would.</summary>
    public IHookErrorLog? HookErrors => graph.Io.HookErrors;

    public IRawFileWriter? Writer => graph.Io.Writer;

    public IMailbox? Mailbox => graph.Mailbox;

    private IRosterWake? _wake;

    public IRosterWake? Wake => _wake ??= new WakeComposition(graph, environment);

    public string? MemberBoxRoot(string teamFile) => Path.GetDirectoryName(graph.Mailbox.BoxDirFor(teamFile, "x"));

    /// <summary>The send is the <c>ah msg send</c> command itself, run in this process, so its resolution, daemon-first delivery and relay text are the CLI's.</summary>
    public BriefSent? SendBrief(string cwd, string member, string team, string refPath)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = CliHost.Create(environment, graph.Runner, new Lazy<ServiceGraph>(graph));
        var code = Verbs.Msg.MsgVerb.Run(context, ["send", $"{member}@{team}", "--ref", refPath, "--kind", "brief", "--cwd", cwd], stdout, stderr);
        if (code != 0)
        {
            throw new InvalidOperationException($"ah msg send {member}@{team} failed ({code}): {stderr.ToString().Trim()}");
        }

        return ParseSent(stdout.ToString());
    }

    /// <summary>Exit 0 means the mail was submitted, so a first line that is not <c>entry &lt;id&gt; …</c> gives a null entry id rather than a failure.</summary>
    internal static BriefSent ParseSent(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var first = lines.Length > 0 ? lines[0].Split(' ') : [];
        var entryId = first.Length >= 2 && first[0] == "entry" ? first[1] : null;
        return new BriefSent(entryId, lines.FirstOrDefault(l => l.StartsWith("delivery:", StringComparison.Ordinal)));
    }

    private sealed class GitScratch(IScratchDirectories inner, IProcessRunner runner) : IRosterScratch
    {
        public string CreateMemberDirectory(string teamHome, string tmpRoot, string member) => inner.CreateMemberDirectory(teamHome, tmpRoot, member);

        public string LinkAlias(string aliasRoot, IReadOnlyList<string> keys, string target) => inner.LinkAlias(aliasRoot, keys, target);

        public string? AliasTarget(string link) => inner.AliasTarget(link);

        public bool RemoveAlias(string aliasRoot, string link) => inner.RemoveAlias(aliasRoot, link);

        public string? Dismiss(string teamHome, string tmpRoot, string member, DateTimeOffset at) => inner.Dismiss(teamHome, tmpRoot, member, at);

        public long Size(string path) => inner.Size(path);

        public IReadOnlyList<DismissedScratch> Dismissed(string teamHome, string tmpRoot) => inner.Dismissed(teamHome, tmpRoot);

        public int SweepDismissed(string teamHome, string tmpRoot, DateTimeOffset now, TimeSpan olderThan) => inner.SweepDismissed(teamHome, tmpRoot, now, olderThan);

        public ScratchSweepReport Sweep(string teamHome, DateTimeOffset now, TimeSpan olderThan, Func<string, bool> isMemberName, Func<string?, string, ScratchOwner> ownerOf, bool commit) =>
            inner.Sweep(teamHome, now, olderThan, isMemberName, ownerOf, commit);

        public bool? IsIgnored(string repoDir, string path)
        {
            var result = runner.Run(new ProcessRequest(
                ["git", "-C", repoDir, "check-ignore", "-q", "--", path],
                Timeout: TimeSpan.FromSeconds(5)));
            if (result.TimedOut)
            {
                return null;
            }

            return result.ExitCode switch { 0 => true, 1 => false, _ => null };
        }

        public string? EnsureWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-probe-" + System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }

        public string? EnsureIgnored(string repoDir, string dir)
        {
            var probe = Path.Combine(dir, ".probe");
            if (IsIgnored(repoDir, probe) == true)
            {
                return null;
            }

            var why = string.Empty;
            string? Git(params string[] args)
            {
                var r = runner.Run(new ProcessRequest(["git", "-C", repoDir, .. args], Timeout: TimeSpan.FromSeconds(5)));
                var text = System.Text.Encoding.UTF8.GetString(r.Stdout.Span).Trim();
                if (r is { ExitCode: 0, TimedOut: false })
                {
                    return text;
                }

                why = $"git {string.Join(' ', args)} exited {r.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "without a status"}: {System.Text.Encoding.UTF8.GetString(r.Stderr.Span).Trim()}";
                return null;
            }

            if (Git("rev-parse", "--git-common-dir") is not { Length: > 0 } common || Git("rev-parse", "--show-prefix") is not { } prefix)
            {
                return $"{IRosterScratch.NotGitRepository} ({repoDir}, so {dir} cannot be ignored: {why})";
            }

            try
            {
                var commonDir = Path.GetFullPath(common, repoDir);
                var line = "/" + prefix.Replace('\\', '/').TrimEnd('/') + "/" + Path.GetRelativePath(repoDir, dir).Replace('\\', '/').TrimEnd('/') + "/";
                var exclude = Path.Combine(commonDir, "info", "exclude");
                var current = File.Exists(exclude) ? File.ReadAllText(exclude) : string.Empty;
                if (!current.Split('\n').Select(l => l.TrimEnd('\r')).Contains(line, StringComparer.Ordinal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
                    File.AppendAllText(exclude, (current.Length == 0 || current.EndsWith('\n') ? string.Empty : "\n") + line + "\n");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"{e.Message}";
            }

            return IsIgnored(repoDir, probe) == true ? null : $"git still does not ignore {dir} after adding it to info/exclude";
        }
    }

    /// <summary>The owner proof for the roster's owner-only verbs, over the live registrations and the team files.</summary>
    public IRosterOwnerProof? OwnerProof => new OwnerProofService(graph, environment);

    public uint? CurrentUserId => OperatingSystem.IsWindows() ? null : PlatformModule.CurrentUid();

    private sealed class OwnerProofService(ServiceGraph g, IAhEnvironment env) : IRosterOwnerProof
    {
        private readonly SessionDirectory _sessions = new(g);

        public OwnerProofResult Check(string dir, string? team)
        {
            // ah2-32: the session the user designated as admin (ah admin grant) passes every owner check
            if (MeshModule.HoldsAdminGrant(_sessions.Table, System.Environment.ProcessId, g.HomeDocs, env.AhHome))
            {
                return new OwnerProofResult(true, null, "admin", false, null);
            }

            var entries = _sessions.Entries(dir);
            var teams = entries.Select(e => e.Entry).ToList();
            TeamOrchestrator? mine = entries.Where(e => e.Raw == team).Select(e => (TeamOrchestrator?)e.Entry).FirstOrDefault();

            if (mine is not { } owner)
            {
                return new OwnerProofResult(false, MeshModule.OwnerVerbRefusal + " " + MeshModule.OwnerVerbStaleSuffix, "stale-registration", true, null);
            }

            return MeshModule.CheckOwnerProof(new OwnerProofInputs(
                _sessions.Table,
                env.GetVariable(MeshModule.MemberMarkerEnv) is { Length: > 0 } marker ? marker : null,
                System.Environment.ProcessId,
                owner,
                teams,
                _sessions.Registered(),
                pid => g.Io.Processes.PidAlive(pid)));
        }

        public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) =>
            _sessions.NearestSession(System.Environment.ProcessId, env.GetVariable("CLAUDE_PID"), _sessions.Teams(dir), rowDirs);

        public IReadOnlyList<OwnerRegistration> RegistrationsOf(int pid)
        {
            var found = new List<OwnerRegistration>();
            foreach (var r in g.Mailbox.LiveRegistrations().Where(r => r.Get("pid") is { IsNumber: true } p && p.AsNumber() == pid))
            {
                var start = r.Get("pid_start") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
                var session = r.Get("session_id") is { IsText: true } id && id.AsString().Length > 0 ? id.AsString() : null;
                found.AddRange(g.Mailbox.RegistrationBoxes(r).Select(b => new OwnerRegistration(b.Address, start, session)));
            }

            return found;
        }

        public IReadOnlyList<LiveSession> LiveSessions()
        {
            var found = new List<LiveSession>();
            foreach (var r in g.Mailbox.LiveRegistrations())
            {
                if (r.Get("pid") is { IsNumber: true } pid && r.Get("session_id") is { IsText: true } session && session.AsString().Length > 0)
                {
                    var start = r.Get("pid_start") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
                    var pane = r.Get("pane_id") is { IsText: true } p && p.AsString().Length > 0 ? p.AsString() : null;
                    found.Add(new LiveSession(session.AsString(), (int)pid.AsNumber(), start, pane, [.. g.Mailbox.RegistrationBoxes(r).Select(b => b.Address)]));
                }
            }

            return found;
        }

        public bool IsUser() => g.Mailbox.PrincipalOf(System.Environment.ProcessId).Kind == PrincipalKind.User;

        public OwnerActor Actor()
        {
            var own = System.Environment.ProcessId;
            return new OwnerActor(
                own,
                _sessions.Table.StartTime(own),
                MeshModule.OwnerChain(_sessions.Table, own, CommandOf),
                string.Join(' ', System.Environment.GetCommandLineArgs()));
        }

        private string? CommandOf(int pid) => _sessions.Table.Command(pid);
    }

    private sealed class EventMailComposition(IMailbox mailbox) : IRosterEventMail
    {
        public IReadOnlyList<string> SendEvent(string cwd, string hierarchyDir, string id, string eventName, string text, JsonObject data, IReadOnlyList<string> recipients)
        {
            var reached = new List<string>();
            foreach (var address in recipients.Distinct(StringComparer.Ordinal))
            {
                var request = MeshModule.EventRequest(address, "ah", cwd, hierarchyDir, eventName, text);
                request["data"] = data;
                request["id"] = id;
                try
                {
                    if (mailbox.Send(request).Ok)
                    {
                        reached.Add(address);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // an unreachable box is left out of the reply
                }
            }

            return reached;
        }
    }

    private sealed class CallerRegistry(IMailCallers callers, IMailbox mailbox, IProcessTable processes) : IRosterCallerRegistry
    {
        /// <summary>
        /// The addresses the session's registration holds after the claim, for this process: a claim with no session id writes
        /// nothing, and one the mailbox refused (held by another live process, or bound) leaves another registration in place.
        /// </summary>
        public IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit) =>
            Register(cwd, sessionId, pid, paneId, teamArg, teamDefaultExplicit, null);

        public IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit, string? herdrFlavour)
        {
            var resolved = callers.RegisterCaller(new CallerRequest(cwd, sessionId, pid, paneId, teamArg, teamDefaultExplicit, herdrFlavour));
            if (string.IsNullOrEmpty(sessionId) || mailbox.ReadRegistration(sessionId) is not { } held
                || held.Get("pid") is not { IsNumber: true } heldPid || heldPid.AsNumber() != pid)
            {
                return [];
            }

            var holds = mailbox.RegistrationBoxes(held).Select(b => b.Address).ToHashSet(StringComparer.Ordinal);
            return resolved.Select(b => b.Address).Where(holds.Contains).ToList();
        }

        public IReadOnlyList<string>? HeldAddresses(string? sessionId, int pid, string? paneId)
        {
            if (string.IsNullOrEmpty(sessionId) || mailbox.ReadRegistration(sessionId) is not { } held
                || held.Get("pid") is not { IsNumber: true } heldPid || heldPid.AsNumber() != pid
                || (!string.IsNullOrEmpty(paneId) && !(held.Get("pane_id") is { IsText: true } heldPane && heldPane.AsString() == paneId)))
            {
                return null;
            }

            return mailbox.RegistrationBoxes(held).Select(b => b.Address).ToList();
        }

        public IReadOnlyList<BoundSession> BoundSessions(int pid) =>
            mailbox.LiveRegistrations()
                .Where(r => r.Get("pid") is { IsNumber: true } p && p.AsNumber() == pid
                    && r.Get("bound") is { Kind: Ah.Core.Json.JsonKind.Bool, IsTruthy: true }
                    && r.Get("session_id") is { IsText: true } s && s.AsString().Length > 0
                    && StartMatches(r, pid)
                    && r.Get("at") is { IsText: true } at && DateTimeOffset.TryParse(at.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                .Select(r => new BoundSession(
                    r.Get("session_id")!.Value.AsString(),
                    DateTimeOffset.Parse(r.Get("at")!.Value.AsString(), System.Globalization.CultureInfo.InvariantCulture)))
                .OrderByDescending(b => b.At)
                .DistinctBy(b => b.SessionId, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// A registration that recorded its process's start time names that process only while the pid still has that start
        /// time: a reused pid has another one. A start time that cannot be read now proves nothing.
        /// </summary>
        private bool StartMatches(Ah.Core.Json.JsonObject registration, int pid) =>
            registration.Get("pid_start") is not { IsText: true } recorded || recorded.AsString().Length == 0
            || Ah.Core.Time.ProcessIdentity.Same(processes, pid, recorded.AsString());

        /// <summary>
        /// The working directories of the live registrations bound to <paramref name="pid"/>. An unbound registration's pid and cwd
        /// are what its registrant claimed, so the directory it names is not a place a session-pids row can be trusted from.
        /// </summary>
        public IReadOnlyList<string> RegistrationCwds(int pid) =>
            mailbox.LiveRegistrations()
                .Where(r => r.Get("pid") is { IsNumber: true } p && p.AsNumber() == pid
                    && r.Get("bound") is { Kind: Ah.Core.Json.JsonKind.Bool, IsTruthy: true }
                    && StartMatches(r, pid))
                .Select(r => r.Get("cwd") is { IsText: true } cwd ? cwd.AsString() : string.Empty)
                .Where(cwd => cwd.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        public DateTimeOffset? ProcessStart(int pid) =>
            Ah.Core.Time.ProcessStart.TryParseUtc(processes.StartTime(pid), out var started) ? started : null;

        public string? ProcessStartText(int pid) => processes.StartTime(pid);

        public IProcessTable? ProcessTable => processes;

        public bool SignalProcess(int pid, int signal) => LaunchModule.IsSignalablePid(pid) && PlatformModule.SignalProcess(pid, signal);

        public bool OwnerProcessIsGone(double pid, string? recordedStart) => callers.OrchestratorProcessIsGone(pid, recordedStart);

        public IReadOnlyList<string> RebindTeams(string cwd, string sessionId, int pid) =>
            callers.RebindOwnedTeams(new RebindRequest(cwd, sessionId, pid, RebindModes.Both)).Changed.Select(x => x.Team).ToList();

        public IReadOnlyList<string> OwnedTeams(string cwd, string? sessionId, int pid) =>
            callers.ResolveCaller(new CallerRequest(cwd, sessionId, pid))
                .Where(b => b.Name == "orchestrator" && b.Team is not null).Select(b => b.Team!).ToList();
    }
}
