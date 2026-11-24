using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Mesh;

public static partial class MeshModule
{
    /// <summary>The environment variable the launcher exports with a member's name; a session that has it is that member whatever its ancestry.</summary>
    public const string MemberMarkerEnv = SessionIdentities.MarkerEnv;

    /// <summary>The refusal an owner-only verb prints when the caller is not the team's Orchestrator.</summary>
    public const string OwnerVerbRefusal = OwnerProof.Refusal;

    /// <summary>The sentence added to the refusal when the team's recorded Orchestrator is stale.</summary>
    public const string OwnerVerbStaleSuffix = OwnerProof.StaleSuffix;

    /// <summary>The code of the refusal given to a caller that is neither the user nor the team's Orchestrator.</summary>
    public const string NotTeamOwner = CallerProof.NotTeamOwner;

    /// <summary>Whether <paramref name="reg"/> is the process the member row names, by the rule mail refuses on: the same pid, and the same start when the row has one.</summary>
    public static bool IsRowProcess(JsonObject row, JsonObject reg) => HeldRow.SamePid(row, reg);

    /// <summary>The way out mail gives a session refused a row held by another process, which is <paramref name="running"/> or gone.</summary>
    public static string HeldRowRemedy(JsonObject row, bool running, string team, string name) => HeldRow.Remedy(row, running, team, name);

    /// <summary>The command that brings <paramref name="member"/> back on its old session from <paramref name="team"/>'s checkpoint (plan only, no <c>--yes</c>).</summary>
    public static string MemberResumeCommand(string team, string member) => MemberHint.Resume(team, member);

    /// <summary>The command that spawns a fresh member of <paramref name="role"/>, on <paramref name="stream"/> when it is a valid stream name.</summary>
    public static string MemberRespawnCommand(string role, string? stream, string team) => MemberHint.Respawn(role, stream, team);

    /// <summary>Whether a checkpoint <paramref name="record"/> lists <paramref name="member"/>, so <see cref="MemberResumeCommand"/> applies to it.</summary>
    public static bool RecordListsMember(JsonValue? record, string member) => MemberHint.Lists(record, member);

    /// <summary>A down member's hint lines: each command on its own line, and the old session id as information.</summary>
    public static IReadOnlyList<string> MemberHintLines(string? resume, string respawn, string? session) => MemberHint.Lines(resume, respawn, session);

    /// <summary>Whether a resolved caller is the user or the session that holds <c>orchestrator@team</c>; any other is refused <see cref="NotTeamOwner"/>.</summary>
    public static CallerVerdict OrchestratorOrUser(CallerPrincipal principal, string? sessionHint, string team) =>
        CallerProof.OrchestratorOrUser(principal, sessionHint, team);

    /// <summary>Whether an owner-only verb runs through the team's Orchestrator: it must be the nearest registered session above the caller, with no member marker.</summary>
    public static OwnerProofResult CheckOwnerProof(OwnerProofInputs input) => OwnerProof.Check(input);

    /// <summary>The ancestors of <paramref name="ownPid"/> with each one's start time and command line, for an owner verb's audit row.</summary>
    public static IReadOnlyList<ChainHop> OwnerChain(IProcessTable table, int ownPid, Func<int, string?> commandOf) =>
        OwnerProof.Chain(table, ownPid, commandOf);

    /// <summary>The nearest ancestor of <paramref name="ownPid"/> that is a registered session or a team's recorded Orchestrator.</summary>
    public static NearestSession NearestRegisteredSession(
        IProcessTable table,
        int ownPid,
        IReadOnlyList<TeamOrchestrator> teams,
        IReadOnlyList<RegisteredSession> registered,
        Func<int, bool>? isSession = null) =>
        AncestorSearch.Nearest(table, ownPid, teams, registered, isSession);

    /// <summary>The session ids <paramref name="pid"/> provably holds by the live registrations of <paramref name="mailbox"/>; null when the process table cannot say.</summary>
    public static IReadOnlyList<string>? CallerSessionIds(IProcessTable table, int pid, IMailbox mailbox) =>
        CallerIdentity.CallerSessions(table, pid, CallerIdentity.LiveSessions(mailbox))?.Select(s => s.SessionId).ToList();

    /// <summary>Every registration of <paramref name="mailbox"/> as its pid and the start time it was registered with.</summary>
    public static IEnumerable<(long Pid, string? Start)> RegisteredProcesses(IMailbox mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return RegisteredProcesses(mailbox.AllRegistrations());
    }

    /// <summary>Every one of <paramref name="registrations"/> as its pid and the start time it was registered with.</summary>
    internal static IEnumerable<(long Pid, string? Start)> RegisteredProcesses(IEnumerable<Ah.Core.Json.JsonObject> registrations) =>
        registrations.Select(static r => (
            r.Get("pid") is { IsNumber: true } p ? (long)p.AsNumber() : 0L,
            r.Get("pid_start") is { IsText: true } s ? s.AsString() : null));

    /// <summary>A session's live registration in <paramref name="mailbox"/> as its pid and start time, or null.</summary>
    public static (long Pid, string Start)? LiveRegistration(IMailbox mailbox, string sessionId) =>
        LiveRegistration(CallerIdentity.LiveSessions(mailbox), sessionId);

    /// <summary>A session's registration among the live <paramref name="sessions"/> as its pid and start time, or null.</summary>
    internal static (long Pid, string Start)? LiveRegistration(IEnumerable<SessionRegistration> sessions, string sessionId) =>
        sessions.FirstOrDefault(s => s.SessionId == sessionId) is { PidStart: { } start } r ? (r.Pid, start) : null;

    /// <summary>
    /// The session a call by <paramref name="principal"/> acts as. <paramref name="hint"/> only selects among the proven sessions;
    /// several and no hint refuse a <paramref name="write"/> as ambiguous and let a read act for all of them.
    /// </summary>
    public static SessionChoice ChooseSession(CallerPrincipal principal, string? hint, bool write) => CallerIdentity.Choose(principal, hint, write);

    /// <summary>Whether the caller may read or act on the box of <paramref name="address"/>: the mail routes' one inbox authority.</summary>
    public static bool MayActFor(CallerPrincipal principal, SessionChoice choice, string address, IMailbox mailbox) =>
        MeshRoutes.MayActFor(principal, choice, address, mailbox);

    /// <summary>
    /// Whether <paramref name="pid"/> was started by the Herdr desktop app for a mod: its direct parent's executable basename is
    /// <c>herdr-app</c> (a cargo build) or <c>Herdr</c> (the installed bundle's <c>Herdr.app/Contents/MacOS/Herdr</c>), and no
    /// process on its whole line is the Claude harness (an unreadable line is not). Rule (b) of the herdr answer verb's caller
    /// check (spec-herdr-desktop.md P3.2).
    /// </summary>
    public static bool IsHerdrAppChild(IProcessTable table, int pid, Func<int, string?> executableOf)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(executableOf);
        return table.Ancestors(pid) is { Count: > 0 } up
            && executableOf(up[0]) is { Length: > 0 } exe && Path.GetFileName(exe.Trim()) is "herdr-app" or "Herdr"
            && CallerIdentity.HarnessOnLine(table, pid) == false;
    }

    /// <summary>The live registrations of <paramref name="mailbox"/> as the caller-identity predicate reads them.</summary>
    public static IReadOnlyList<SessionRegistration> LiveSessions(IMailbox mailbox) => CallerIdentity.LiveSessions(mailbox);

    /// <summary>
    /// Whether a hook's payload session does not belong to the hook's process: it names a registered session that is not among
    /// the sessions <paramref name="parentPid"/> provably holds, or that process holds sessions and this is not one of them. A
    /// session with no registration, from a process that holds none, is not a mismatch: a non-participant, or one not yet
    /// registered.
    /// </summary>
    public static bool HookSessionMismatch(IProcessTable table, int parentPid, string sessionId, IReadOnlyList<SessionRegistration> live)
    {
        var proven = CallerIdentity.CallerSessions(table, parentPid, live) ?? [];
        if (proven.Any(s => s.SessionId == sessionId))
        {
            return false;
        }

        return proven.Count > 0 || live.Any(s => s.SessionId == sessionId);
    }

    /// <summary>
    /// Why a SessionStart for <paramref name="sessionId"/> run by harness process <paramref name="parentPid"/> must write nothing, or
    /// null when it may run: a pid not above 1, a process whose command is not the Claude harness, or a session whose registration
    /// is live on another process. A registration whose process is gone (a resume) does not refuse.
    /// </summary>
    public static string? SessionStartRefusal(IProcessTable table, int parentPid, string sessionId, IReadOnlyList<SessionRegistration> live)
    {
        if (parentPid <= 1)
        {
            return "no harness process";
        }

        if (!HarnessCommand.IsHarnessProcess(parentPid, table.Command))
        {
            return "the parent process is not a Claude harness";
        }

        return sessionId.Length > 0 && live.Any(r => r.SessionId == sessionId && r.Pid != parentPid && ProcessIdentity.Same(table, r.Pid, r.PidStart))
            ? "the session is live on another process"
            : null;
    }

    /// <summary>Whether a process command line is the Claude harness: <c>claude</c>, or node or bun running a claude-code path.</summary>
    public static bool IsHarnessCommand(string? commandLine) => HarnessCommand.Matches(commandLine);

    /// <summary>The Claude harness processes on this machine, every repo and non-ah session included; null when the process table cannot be read.</summary>
    public static int? HarnessCount(IProcessTable processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return processes.All() is { } all ? all.Count(static p => IsHarnessCommand(p.Command)) : null;
    }

    /// <summary>
    /// Whether <paramref name="pid"/> is a Claude harness session: <paramref name="claudePid"/> (the environment's harness pid)
    /// is unset or this pid, a session-pids row exists for it from its start on, and its command is the harness.
    /// </summary>
    public static bool IsHarnessSession(
        int pid,
        string? claudePid,
        Func<int, DateTimeOffset?> startOf,
        Func<int, DateTimeOffset, bool> hasRowSince,
        Func<int, string?> commandOf,
        Func<int, string?>? executableOf = null) =>
        HarnessCommand.IsSession(pid, claudePid, startOf, hasRowSince, commandOf, executableOf);

    /// <summary>ah2-32: whether <paramref name="callerPid"/> calls from the session the user designated as admin. False when the database cannot be read.</summary>
    public static bool HoldsAdminGrant(IProcessTable table, int? callerPid, IHomeDocs? docs, string ahHome) => AdminGrant.Holds(table, callerPid, docs, ahHome);

    /// <summary>ah2-32: records the admin grant for the Claude session process <paramref name="pid"/> in the state database; an error text when it cannot.</summary>
    public static string? WriteAdminGrant(IProcessTable table, IHomeDocs? docs, string ahHome, int pid, string? sessionId, DateTimeOffset at, string via = AdminGrant.ViaTerminal) =>
        AdminGrant.Write(table, docs, ahHome, pid, sessionId, at, via);

    /// <summary>ah2-499: how a grant is recorded when a typed <c>/wrangl:admin</c> prompt made it.</summary>
    public const string AdminGrantViaPrompt = AdminGrant.ViaPrompt;

    /// <summary>ah2-499: the grant's <c>via</c>; a row without one reads as <c>terminal</c>.</summary>
    public static string AdminGrantVia(string? body) => AdminGrant.ViaOf(body);

    /// <summary>ah2-499 (admin-remote A3.2): why a harness may not take a grant from a typed prompt (nested, print/stream, no terminal), or null.</summary>
    public static string? AdminPromptGrantRefusal(IProcessTable table, int harness, Func<int, string?> executableOf) => AdminGrant.PromptGrantRefusal(table, harness, executableOf);

    /// <summary>ah2-499: the pid a grant names; null when there is none or it cannot be read.</summary>
    public static int? AdminGrantPid(string? body) => AdminGrant.PidOf(body);

    /// <summary>ah2-32: removes the admin grant; an error text when the database refuses.</summary>
    public static string? RevokeAdminGrant(IHomeDocs? docs, string ahHome) => AdminGrant.Revoke(docs, ahHome);

    /// <summary>ah2-46: the recorded grant's text, null when there is none; false when the database cannot answer.</summary>
    public static bool TryReadAdminGrant(IHomeDocs? docs, string ahHome, out string? body) => AdminGrant.TryRead(docs, ahHome, out body);

    /// <summary>ah2-46: takes a grant left in <c>admin.json</c> into the database and removes the file; a text when it could not.</summary>
    public static string? ImportAdminGrant(IHomeDocs? docs, string ahHome) => AdminGrant.Import(docs, ahHome);

    /// <summary>ah2-32: the nearest Claude harness process on the caller's line, registered or not; null when there is none.</summary>
    public static int? HarnessOf(IProcessTable table, int? callerPid) => AdminGrant.HarnessOf(table, callerPid);

    /// <summary>The command half of <see cref="IsHarnessSession"/>: the executable's basename is <c>claude</c>, or the command line is the harness. It reads no session record.</summary>
    public static bool IsHarnessProcess(int pid, Func<int, string?> commandOf, Func<int, string?>? executableOf = null) =>
        HarnessCommand.IsHarnessProcess(pid, commandOf, executableOf);
}
