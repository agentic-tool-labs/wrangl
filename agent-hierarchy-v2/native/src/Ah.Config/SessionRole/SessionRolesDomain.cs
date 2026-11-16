using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Config;

/// <summary>
/// The <c>session-roles</c> state domain: which hierarchy role each session was started as, kept as the
/// <see cref="SessionMapFile.RolesKey"/> <c>meta</c> row (b397 group 2). Written only through <c>set</c>; <c>get</c> is a read.
/// An entry records the process (pid and start time) that set it, and its role cannot change while that process is alive. An entry
/// with no process recorded is pinned, on the next write, to its session's live registration, or dropped when it has none.
/// </summary>
[StateDomain("session-roles", "session-roles")]
internal sealed class SessionRolesDomain(
    IHomeDocs? docs,
    IAhEnvironment environment,
    IClock clock,
    IProcessTable? processes = null,
    Func<string, (long Pid, string Start)?>? liveRegistration = null) : IStateDomain
{
    internal const string Pinned = "role_pinned: the session's role is recorded for a live process and cannot change";

    internal const int Version = 1;

    private string Home => environment.HomeDirectory;

    public string Name => "session-roles";

    public IReadOnlyList<string> StoreNames => ["session-roles"];

    public bool HomeScoped => true;

    public bool DbOnly => true;

    public string StateDirectory(RequestContext context) => NodePath.Join(AhPaths.UserEntry(Home, AhPaths.SessionRolesStateDir), "session-roles");

    public IReadOnlyList<StateOp> Ops =>
    [
        new StateOp("set", OpKind.Write, OpStyle.Replace, false, null, true, DbOnlyBody) { DbBody = Set },
        new StateOp("get", OpKind.Read, null, false, null, true, Get),
        new StateOp("orchestrator", OpKind.Write, OpStyle.Replace, false, null, true, DbOnlyBody) { DbBody = Orchestrator },
    ];

    public void AfterWrite(OpCall operation, OpResult result)
    {
    }

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"session-roles.{call.Op} is db-only", "op_failed");

    /// <summary>
    /// The session's recorded role, or null. An authority: the ah CLI gate restricts a session by it. A subagent shares its parent's
    /// session id, so it is the main session's role.
    /// </summary>
    internal string? RoleOf(JsonValue? sessionId)
    {
        var entry = SessionMapFile.Parse(docs?.Read(SessionMapFile.RolesKey), Version)["sessions"].AsObject().Get(SessionMapFile.KeyOf(sessionId));
        return entry is { IsMap: true } e && e.AsObject().Get("role") is { IsText: true } role ? role.AsString() : null;
    }

    private static JsonObject ArgsOf(OpCall call) =>
        JsonText.Parse(call.ArgsJson.Span) is { IsMap: true } v ? v.AsObject() : throw new InvalidOperationException("args must be an object");

    private static OpResult Ok(JsonValue value) => new(true, JsonText.ToUtf8(value, 0));

    private OpResult Set(OpCall call, IDbOpTransaction tx)
    {
        try
        {
            var args = ArgsOf(call);
            var sessionId = args.Get("session_id");
            var key = SessionMapFile.KeyOf(sessionId is { IsNull: true } ? null : sessionId);
            var role = args.Get("role");
            var state = SessionMapFile.Read(tx, SessionMapFile.RolesKey, Version);
            var sessions = state["sessions"].AsObject();
            PinOrDropUnpinned(sessions);
            if (sessions.Get(key) is { IsMap: true } existing && HeldByLiveProcess(existing.AsObject()))
            {
                if (TextOf(existing.AsObject().Get("role")) != TextOf(role))
                {
                    return new OpResult(false, default, Pinned);
                }
            }
            else
            {
                var entry = new JsonObject();
                if (role is not null)
                {
                    entry["role"] = role.Value;
                }

                entry["at"] = Timestamps.ToIsoString(clock.UtcNow);
                if (args.Get("pid") is { IsNumber: true } pid && double.IsInteger(pid.AsNumber()) && pid.AsNumber() > 1
                    && args.Get("pid_start") is { IsText: true } start && start.AsString().Length > 0)
                {
                    entry["pid"] = pid;
                    entry["pid_start"] = start;
                }

                sessions[key] = entry;
            }

            state["sessions"] = SessionMapFile.Prune(sessions);
            SessionMapFile.Put(tx, SessionMapFile.RolesKey, state);
            var result = new JsonObject { ["session_id"] = key };
            if (role is not null)
            {
                result["role"] = role.Value;
            }

            return Ok(result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new OpResult(false, default, e.Message);
        }
    }

    /// <summary>
    /// The Orchestrator-by-command flag of one session, in one write. <c>action</c> is <c>on</c> (flag the session and record the
    /// owning process, which must be given, and clear a decline), <c>off</c> (clear the flag and record the decline), <c>restamp</c> (record a new owning process on a flagged session) or
    /// <c>migrate</c> (move the flag from the flagged entry owned by exactly this pid and start time to this session). The result
    /// is <c>{changed}</c>, which reports the flag only; nothing is written when nothing changed.
    /// </summary>
    private OpResult Orchestrator(OpCall call, IDbOpTransaction tx)
    {
        try
        {
            var args = ArgsOf(call);
            var sessionId = args.Get("session_id");
            var key = SessionMapFile.KeyOf(sessionId is { IsNull: true } ? null : sessionId);
            if (key == SessionMapFile.NoSessionKey)
            {
                return new OpResult(false, default, "no session id");
            }

            var owner = ProcessOf(args);
            var state = SessionMapFile.Read(tx, SessionMapFile.RolesKey, Version);
            var sessions = state["sessions"].AsObject();
            var at = Timestamps.ToIsoString(clock.UtcNow);
            var action = TextOf(args.Get("action"));
            var changed = action switch
            {
                "on" => owner is { } on ? Flag(sessions, key, on, at, always: false) : throw new InvalidOperationException("the owning process is required"),
                "off" => Unflag(sessions, key),
                "restamp" => owner is { } stamp && IsFlagged(sessions.Get(key)) && Flag(sessions, key, stamp, at, always: false),
                "migrate" => owner is { } move && Migrate(sessions, key, move, at),
                _ => throw new InvalidOperationException("unknown action"),
            };
            var declined = action == "off" && Decline(sessions, key, owner, at);
            if (changed || declined)
            {
                state["sessions"] = SessionMapFile.Prune(sessions);
                SessionMapFile.Put(tx, SessionMapFile.RolesKey, state);
            }

            return Ok(new JsonObject { ["changed"] = changed });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new OpResult(false, default, e.Message);
        }
    }

    private static bool IsFlagged(JsonValue? entry) =>
        entry is { IsMap: true } e && e.AsObject().Get(SessionMapFile.OrchestratorField) is { IsTruthy: true };

    /// <summary>Flags the session and records its owner. False when it was already flagged with that owner.</summary>
    private static bool Flag(JsonObject sessions, string key, (long Pid, string Start) owner, string at, bool always)
    {
        var entry = sessions.Get(key) is { IsMap: true } existing ? existing.AsObject() : new JsonObject();
        if (!always && IsFlagged(entry) && ProcessOf(entry) is { } held && held == owner)
        {
            return false;
        }

        _ = entry.Remove(SessionMapFile.DeclinedField);
        entry[SessionMapFile.OrchestratorField] = true;
        entry["at"] = at;
        entry["pid"] = owner.Pid;
        entry["pid_start"] = owner.Start;
        sessions[key] = entry;
        return true;
    }

    /// <summary>Clears the flag; an entry that holds no role is then removed. False when the session was not flagged.</summary>
    private static bool Unflag(JsonObject sessions, string key)
    {
        if (sessions.Get(key) is not { IsMap: true } existing || !IsFlagged(existing))
        {
            return false;
        }

        var entry = existing.AsObject();
        _ = entry.Remove(SessionMapFile.OrchestratorField);
        if (entry.Get("role") is not { IsText: true })
        {
            _ = sessions.Remove(key);
        }

        return true;
    }

    /// <summary>Records that the user turned the session off, so an implicit opt-in leaves it alone, and the process, so /clear carries the decline. False when it already says so.</summary>
    private static bool Decline(JsonObject sessions, string key, (long Pid, string Start)? owner, string at)
    {
        var entry = sessions.Get(key) is { IsMap: true } existing ? existing.AsObject() : new JsonObject();
        if (entry.Get(SessionMapFile.DeclinedField) is { IsTruthy: true })
        {
            return false;
        }

        entry[SessionMapFile.DeclinedField] = true;
        entry["at"] = at;
        if (owner is { } process)
        {
            entry["pid"] = process.Pid;
            entry["pid_start"] = process.Start;
        }

        sessions[key] = entry;
        return true;
    }

    /// <summary>Moves a decline to <paramref name="key"/> from the entries of exactly <paramref name="owner"/>. False when none declined.</summary>
    private static bool MigrateDecline(JsonObject sessions, string key, (long Pid, string Start) owner, string at)
    {
        var matches = sessions.Keys
            .Where(k => k != key && sessions.Get(k) is { IsMap: true } e && e.AsObject().Get(SessionMapFile.DeclinedField) is { IsTruthy: true } && ProcessOf(e.AsObject()) is { } held && held == owner)
            .ToList();
        foreach (var match in matches)
        {
            var entry = sessions.Get(match)!.Value.AsObject();
            _ = entry.Remove(SessionMapFile.DeclinedField);
            if (entry.Get("role") is not { IsText: true })
            {
                _ = sessions.Remove(match);
            }
        }

        return matches.Count > 0 && Decline(sessions, key, owner, at);
    }

    /// <summary>Moves the flag to <paramref name="key"/> from the flagged entries owned by exactly <paramref name="owner"/>; the newest by <c>at</c> decides, and all of them are cleared.</summary>
    private static bool Migrate(JsonObject sessions, string key, (long Pid, string Start) owner, string at)
    {
        var matches = sessions.Keys
            .Where(k => k != key && sessions.Get(k) is { IsMap: true } e && IsFlagged(e) && ProcessOf(e.AsObject()) is { } held && held == owner)
            .ToList();
        if (matches.Count == 0)
        {
            return MigrateDecline(sessions, key, owner, at);
        }

        foreach (var match in matches)
        {
            _ = Unflag(sessions, match);
        }

        return Flag(sessions, key, owner, at, always: true);
    }

    private static string? TextOf(JsonValue? value) => value is { IsText: true } text ? text.AsString() : null;

    private static (long Pid, string Start)? ProcessOf(JsonObject entry) =>
        entry.Get("pid") is { IsNumber: true } pid && double.IsInteger(pid.AsNumber()) && pid.AsNumber() > 1
        && entry.Get("pid_start") is { IsText: true } start && start.AsString().Length > 0
            ? ((long)pid.AsNumber(), start.AsString())
            : null;

    /// <summary>
    /// Whether the entry's recorded process is alive; with no process table to ask, it is taken to be. An entry that records no
    /// process is held when there is no registration lookup to pin it by.
    /// </summary>
    private bool HeldByLiveProcess(JsonObject entry) =>
        ProcessOf(entry) is { } held
            ? processes is null || ProcessIdentity.Same(processes, held.Pid, held.Start)
            : liveRegistration is null;

    private void PinOrDropUnpinned(JsonObject sessions)
    {
        if (liveRegistration is null)
        {
            return;
        }

        foreach (var key in sessions.Keys.ToList())
        {
            if (sessions.Get(key) is not { IsMap: true } entry || ProcessOf(entry.AsObject()) is not null)
            {
                continue;
            }

            if (liveRegistration?.Invoke(key) is { } registration && registration.Pid > 1 && registration.Start.Length > 0)
            {
                entry.AsObject()["pid"] = registration.Pid;
                entry.AsObject()["pid_start"] = registration.Start;
            }
            else
            {
                _ = sessions.Remove(key);
            }
        }
    }

    private OpResult Get(OpCall call, IStoreWriteContext? context)
    {
        try
        {
            var sessionId = ArgsOf(call).Get("session_id");
            return Ok(RoleOf(sessionId is { IsNull: true } ? null : sessionId) is { } role ? role : JsonValue.Null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new OpResult(false, default, e.Message);
        }
    }
}
