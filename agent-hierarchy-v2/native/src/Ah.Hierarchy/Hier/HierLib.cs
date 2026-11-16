using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Hierarchy;

/// <summary>
/// What the libraries of this assembly are built from. The composition root supplies it once: <c>Writer</c> is the state writer a
/// library hands its writes to (null for read-only use) and <c>Process</c> the calling process's working directory and parent pid.
/// <c>DecisionRule</c> answers (hierarchy dir, key) with the stored decision rule of that key the decision validator checks a
/// <c>user-rule</c> decider against; without it no line may name a rule.
/// </summary>
public sealed record HierarchyEnvironment(
    IFileReader Files,
    IAhEnvironment Environment,
    IClock Clock,
    IRandom Random,
    IProcessInspector Processes,
    IConfigCore Config,
    IRosterCore Roster,
    IStateWriter? Writer,
    ProcessFacts Process,
    IHierarchyBootstrap? Bootstrap = null,
    IHookErrorLog? Errors = null,
    Func<string, JsonValue, string?>? MemberVerdict = null,
    PeerRecordsRoute? PeerRoute = null,
    MsgsRoute? MsgsRoute = null,
    Func<string, BacklogHealth?>? BacklogHealth = null,
    Func<string, PlanState?>? BacklogPlan = null,
    IStatusEntryStore? StatusEntries = null,
    Func<string, JsonValue?>? BacklogFacts = null,
    ISessionAuthority? Sessions = null,
    Func<string, string, RuleRow?>? DecisionRule = null,
    ISessionPidSource? SessionPids = null,
    IActivitySource? Activity = null,
    IGateSource? Gates = null,
    IQaLogSource? QaLogRows = null,
    IRosterRecordSource? RosterRecords = null,
    IJobSource? Jobs = null,
    IDecisionSource? Decisions = null,
    IProgressLogSource? ProgressLogs = null,
    ICheckpointSource? Checkpoints = null,
    IProgressDocSource? ProgressDocs = null,
    Func<IReadOnlyList<string>>? QaJobBodies = null,
    Func<IReadOnlyList<KeyValuePair<string, string>>>? KnownDirRows = null,
    IUsageRows? UsageRows = null,
    Func<string, long?>? DomainSeq = null);

/// <summary>
/// Where the messages live when the <c>msgs</c> domain is in <c>db</c> mode, with the same meaning of <c>Modes</c> and
/// <c>MarkerSaysDb</c> as <see cref="PeerRecordsRoute"/>. Without a route the message files are the only backend.
/// </summary>
public sealed record MsgsRoute(Ah.Core.Json.IMsgSource Source, IMessageModeReader Modes, Func<bool>? MarkerSaysDb = null);

/// <summary>
/// Where the peer records live when the <c>peer-records</c> domain is in <c>db</c> mode: <c>Modes</c> says which backend is in force
/// and <c>Source</c> reads the database. Without a route the peer log file is the only backend. <c>MarkerSaysDb</c> answers, when the
/// mode cannot be read, whether the authority marker beside the database says the domain was flipped (an unknown answer, or no
/// delegate, counts as flipped): only a domain still on files may then be read from the file.
/// </summary>
public sealed record PeerRecordsRoute(Ah.Core.Json.IPeerRecordSource Source, IMessageModeReader Modes, Func<bool>? MarkerSaysDb = null);

/// <summary>The libraries of the hierarchy runtime directory: messages, gates, the peer roster, status, decisions and peer records.</summary>
public sealed record HierarchyServices(
    IHierarchyCore Core,
    IHierarchyMessages Messages,
    IHierarchyRoster Roster,
    IHierarchyPeers Peers,
    IHierarchyStatus Status,
    IHierarchyDecisions Decisions,
    IReadOnlyList<IStateDomain> StateDomains,
    IStatusRefresher StatusRefresher,
    StateCalls.IHierarchyStateCalls StateCalls,
    IHierarchyProgress Progress);

/// <summary>The one implementation of lib-hier, lib-peer, lib-status and lib-decisions: they import each other, so they share a class.</summary>
internal sealed partial class HierLib
{
    private readonly IFileReader _files;
    private readonly IAhEnvironment _environment;
    private readonly IClock _clock;
    private readonly IConfigCore _config;
    private readonly IRosterCore _roster;
    private readonly ISessionAuthority? _sessions;
    private readonly IStateWriter? _writer;
    private readonly string _cwd;
    private readonly IHierarchyBootstrap? _bootstrap;
    private readonly IHookErrorLog? _errors;
    private readonly Func<string, JsonValue, string?>? _memberVerdict;
    private readonly HierarchyCore _core;
    private readonly IMessageModeReader? _messageModes;
    private readonly Func<string, BacklogHealth?>? _backlogHealth;
    private readonly Func<string, PlanState?>? _backlogPlan;
    private readonly IStatusEntryStore? _statusEntries;
    private readonly Func<string, JsonValue?>? _backlogFacts;
    private readonly Func<string, string, RuleRow?>? _decisionRule;
    private readonly ISessionPidSource? _sessionPids;
    private readonly IActivitySource? _activity;
    private readonly IGateSource? _gates;
    private readonly IQaLogSource? _qaLogRows;
    private readonly IRosterRecordSource? _rosterRecords;
    private readonly IJobSource? _jobs;
    private readonly IDecisionSource? _decisions;
    private readonly IProgressLogSource? _progressLogs;
    private readonly ICheckpointSource? _checkpoints;
    private readonly IProgressDocSource? _progressDocs;
    private readonly Func<IReadOnlyList<string>>? _qaJobBodies;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>>? _knownDirRows;
    private readonly Func<string, long?>? _domainSeq;

    internal HierLib(HierarchyEnvironment e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _qaJobBodies = e.QaJobBodies;
        _knownDirRows = e.KnownDirRows;
        _domainSeq = e.DomainSeq;
        _decisions = e.Decisions;
        _decisionRule = e.DecisionRule;
        _sessionPids = e.SessionPids;
        _activity = e.Activity;
        _gates = e.Gates;
        _qaLogRows = e.QaLogRows;
        _rosterRecords = e.RosterRecords;
        _jobs = e.Jobs;
        _progressLogs = e.ProgressLogs;
        _checkpoints = e.Checkpoints;
        _progressDocs = e.ProgressDocs;
        _statusEntries = e.StatusEntries;
        _backlogFacts = e.BacklogFacts;
        _messageModes = e.PeerRoute?.Modes;
        _backlogHealth = e.BacklogHealth;
        _backlogPlan = e.BacklogPlan;
        _files = e.Files;
        _environment = e.Environment;
        _clock = e.Clock;
        _config = e.Config;
        _roster = e.Roster;
        _sessions = e.Sessions;
        _writer = e.Writer;
        _cwd = e.Process.CurrentDirectory;
        _bootstrap = e.Bootstrap;
        _errors = e.Errors;
        _memberVerdict = e.MemberVerdict;
        _core = new HierarchyCore(e.Files, e.Environment, e.Clock, e.Random, e.Processes, e.PeerRoute, e.Errors, e.MsgsRoute, e.Roster);

        // b459: the long-lived caches of the state reads in the health census
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.status_parsed_rows", static l => { lock (l._heldEntries) { return l._heldEntries.Values.Sum(static h => h.Entries.Count); } });
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.gate_times.dirs", static l => { lock (l._gateTimesGate) { return l._gateTimes.Count; } });
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.gate_times.requests", static l => { lock (l._gateTimesGate) { return l._gateTimes.Values.Sum(static v => (long)v.ById.Count); } });
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.plan_folds", static l => l._planFolds.Count, static l => l._planFolds.Values.Sum(static v => 2L * v.Text.Length));
        Ah.Core.CoreModule.CensusAdd(this, "hierarchy.main_pools", static l => l._mainPools.Count);
    }

    internal HierarchyCore Core => _core;

    private double NowMs => _clock.UtcNow.ToUnixTimeMilliseconds();

    private bool Exists(string path)
    {
        try
        {
            return _files.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><c>readFileSync(path, "utf8")</c>, or null when it cannot be read.</summary>
    private string? TryRead(string path)
    {
        try
        {
            return _files.ReadText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The stored text of a message; null when it cannot be read. A store that cannot be read inside a decision scope is not swallowed.
    /// </summary>
    private string? TryReadMsg(string path)
    {
        try
        {
            return _core.ReadMsgTextShared(path);
        }
        catch (Exception e) when (e is not StoreUnavailableException)
        {
            return null;
        }
    }

    private static JsonValue Or(JsonValue? value, JsonValue fallback) => value is { IsTruthy: true } v ? v : fallback;

    private static JsonValue Member(JsonValue? value, string name) => value?.Member(name) ?? JsonValue.Null;

    private static string? TextOf(JsonValue? value) => value is { IsText: true } v ? v.AsString() : null;

    /// <summary><c>path.resolve</c> against the process's working directory.</summary>
    private string Resolve(string path) => NodePath.Resolve(_cwd, path);

    private bool IsUnder(string path, string dir)
    {
        // by key: a db row comes back in the real spelling while the caller may hold a linked one
        var p = MsgRows.PoolKey(Resolve(path));
        var d = MsgRows.PoolKey(Resolve(dir));
        var rel = p.Length >= d.Length ? p[d.Length..] : string.Empty;
        return p.StartsWith(d, StringComparison.Ordinal) && (rel.Length == 0 || rel.StartsWith('/'));
    }

    /// <summary>The team guide the plugin ships, named in every message that names a team file.</summary>
    public string TeamGuide =>
        _environment.Plugin is { } plugin
            ? NodePath.Join(plugin.Root, "docs", "team-file.md")
            : throw new InvalidOperationException("the plugin root is unknown");

    /// <summary>
    /// Creates, before a project-scoped write, the hierarchy directories of <paramref name="context"/> that are a repository's own
    /// default directory, are missing, and whose repository has ah2 enabled. A team home or pool elsewhere is never created, and
    /// neither is anything in a repository that has not enabled ah2. A failure is logged and the write goes on: the daemon then
    /// refuses it and says so in its rejection log. A write to a db-only <paramref name="domain"/> never creates a linked work
    /// tree's directory: the daemon accepts it missing.
    /// </summary>
    internal void EnsureProjectDirs(RequestContext context, bool hook = false, string? domain = null)
    {
        var dbOnly = domain is not null && _writer?.IsDbOnly(domain) == true;
        if (_bootstrap is null)
        {
            return;
        }

        foreach (var dir in new[] { context.HierarchyDirectory, context.MainHierarchyDirectory })
        {
            try
            {
                if (dir.Length == 0 || Exists(dir) || AhPaths.RepoRootOf(dir) is not { } root || _config.HierarchyDir(root) != dir || (dbOnly && IsLinkedTreeDir(dir)))
                {
                    continue;
                }

                // SessionStart's reading: a repository nobody configured is not enabled, whatever the default of `enabled` is
                var resolved = _config.ResolveConfig(root);
                if (resolved.Get("configured") is not { IsTruthy: true } || resolved.Get("enabled") is not { IsTruthy: true })
                {
                    continue;
                }

                if (hook && _environment.TestMode
                    && !Ah.Core.Text.RealPathResolver.IsUnderProbeSandbox(Ah.Core.Text.RealPathResolver.ThroughLongestExistingPrefix(_files, dir)))
                {
                    _errors?.Log("state-calls", $"test-mode write outside the sandbox skipped: {dir}");
                    continue;
                }

                _ = _bootstrap.EnsureHierarchyDir(dir);
            }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
            {
                _errors?.Log("state-calls", $"could not create the hierarchy directory {dir}: {e.Message}");
            }
        }
    }

    /// <summary>The <see cref="Exception.Data"/> key under which a failed route-state call carries its error code.</summary>
    internal const string ErrorCodeKey = "error_code";

    /// <summary>A route-state call that must succeed: a failed one is thrown, as the synchronous Node entry throws.</summary>
    private StateResult Call(RequestContext context, string domain, string op, JsonValue args, bool hook = false)
    {
        EnsureProjectDirs(context, hook, domain);
        var writer = _writer ?? throw new InvalidOperationException("no state writer is configured");
        var result = writer.Invoke(domain, op, JsonText.ToUtf8(args, 0), context, hook ? new StateCallOptions(Hook: true) : null);
        if (!result.Ok)
        {
            var failure = new InvalidOperationException(result.Error ?? result.ErrorCode ?? "state call failed");
            failure.Data[ErrorCodeKey] = result.ErrorCode;
            throw failure;
        }

        return result;
    }

    /// <summary>The request context of a write into pool <paramref name="dir"/>: its checkout's context, with <paramref name="dir"/> as the hierarchy dir.</summary>
    public RequestContext PoolContext(string dir)
    {
        // A pool outside any repository (~/.claude/ah2/hierarchy/<name>) is derived from itself: this process's own cwd would claim
        // another pool, and the daemon refuses that claim (hierarchy_dir is not derived from cwd).
        var root = AhPaths.RepoRootOf(dir) ?? (AhPaths.IsUserProjectDir(dir) && _config.FindGitRoot(dir) is null ? dir : _cwd);
        var socket = _environment.GetVariable("CLAUDE_CODE_MESSAGING_SOCKET");
        return new RequestContext(
            root,
            dir,
            _config.MainHierarchyDir(root) ?? string.Empty,
            string.IsNullOrEmpty(socket) ? string.Empty : NodePath.Dirname(socket));
    }
}
