using Ah.Core.Json;
using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Hooks;

/// <summary>
/// Everything a native hook body reads or writes. The composition root builds it from the platform services; a hook
/// receives it as an argument and holds no static state of its own.
/// </summary>
public sealed record GateServices(
    IAhEnvironment Environment,
    IClock Clock,
    IFileReader Files,
    IConfigCore Config,
    IRosterCore Roster,
    HierarchyServices Hierarchy,
    IHierarchyBootstrap Bootstrap,
    IStateWriter State,
    IHookErrorLog ErrorLog,
    ProcessFacts Process,
    IMeshGate Mesh,
    IConventions Conventions,
    AdvisoryServices? Advisory = null,
    IGateSessions? Sessions = null,
    Func<string, JsonValue, bool>? MemberUp = null,
    Func<string, string, string, IReadOnlyList<string>, IReadOnlyList<string>>? ReadyToDismiss = null,
    Action<string, string?, string, string>? WorkCommit = null,
    Func<string, RulesSubject, string?>? RulesBlock = null,
    Func<string, string?>? AdrWaiting = null);

/// <summary>
/// Who a session's rules are read for: its team, read in <paramref name="TeamDir"/> when set, and with <paramref name="Parent"/> the next link of
/// the team's chain whether or not the team's record exists yet.
/// </summary>
public sealed record RulesSubject(string? Team, IReadOnlySet<string> Names, bool Orchestrator, string? TeamDir = null, string? Parent = null);

/// <summary>
/// What the advisory hooks (session lifecycle, prompt and activity tracking) need beyond a gate: the mailbox and its hook-side
/// helpers, the caller helpers, the mail rules, the process runner, the daemon client, the team-intent phrases and the install-stamp host. Built by the composition root with the rest.
/// </summary>
public sealed record AdvisoryServices(
    IMailbox Mailbox,
    IMailboxHooks MailboxHooks,
    IMailCallers Callers,
    IMailCore MailCore,
    IProcessRunner Runner,
    IDirectiveText Directive,
    IHierarchyContext Context,
    IHierarchyRecovery Recovery,
    IDaemonClient Daemon,
    ITeamIntent TeamIntent,
    IInstallStampHost InstallStamp,
    ITurnEndLog? TurnEnds = null,
    IMeshSender? Sender = null);

/// <summary>The processes and registered sessions a gate relates to a pid to say who a session is.</summary>
public interface IGateSessions
{
    /// <summary>The process table: ancestry and start times.</summary>
    IProcessTable Table { get; }

    /// <summary>The registered orchestrator of every team of the hierarchy directory.</summary>
    IReadOnlyList<TeamOrchestrator> Teams(string hierarchyDir);

    /// <summary>The live registered sessions and their addresses.</summary>
    IReadOnlyList<RegisteredSession> Registered();

    /// <summary>The live registrations as the caller-identity predicate reads them: session, process, recorded start, addresses.</summary>
    IReadOnlyList<SessionRegistration> Sessions() => [];
}
