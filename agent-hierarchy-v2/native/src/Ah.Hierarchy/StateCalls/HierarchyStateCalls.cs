using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Hierarchy.StateCalls;

/// <summary>What a team-file mutation decided: replace the record, or remove the file (<see cref="Record"/> null). A mutation that returns null leaves the file as it is.</summary>
public sealed record TeamFileOutcome(JsonValue? Record)
{
    public bool IsClear => Record is null;

    public static TeamFileOutcome Write(JsonValue team) => new(team);

    public static TeamFileOutcome Clear() => new((JsonValue?)null);
}

/// <summary>
/// The call-site entries of the team, peer-record, message, roster, gate, decision, merge-record and usage state domains: each runs
/// one domain operation through the state writer and returns what the operation returned. A failed call is thrown as
/// an <see cref="InvalidOperationException"/> whose <c>Data["code"]</c> is the route's code (<c>conflict</c>, <c>lock_timeout</c>, <c>op_failed</c> and the like). Entries that always run inside a hook (peer records, gates, merge records, usage) use
/// budget mode unless <c>options</c> says otherwise; a non-null <c>options</c> is used as given.
/// </summary>
public interface IHierarchyStateCalls
{
    /// <summary>
    /// Updates the team file <paramref name="path"/> of pool <paramref name="dir"/> against what it holds now. <paramref name="mutate"/>
    /// gets the file's record (null when there is none) and is a pure function of it: it may be called again on a fresher document
    /// after a conflict, up to five times. When the written record names this process's parent as its Orchestrator, the calling
    /// session is bound to that pid in the session-pids log.
    /// </summary>
    StateUpdateResult UpdateTeamFile(string path, string dir, Func<JsonValue, TeamFileOutcome?> mutate, StateCallOptions? options = null);

    /// <summary><see cref="UpdateTeamFile"/> for the team file <paramref name="team"/> (the default team when null) of pool <paramref name="dir"/>.</summary>
    StateUpdateResult UpdateTeam(string dir, string? team, Func<JsonValue, TeamFileOutcome?> mutate, StateCallOptions? options = null);

    /// <summary>Appends one row to the HOME-global peer-pending file.</summary>
    void AppendPeerRecord(JsonValue record, StateCallOptions? options = null);

    /// <summary>Creates the message file <c>args.path</c>: <c>{dir, path, content, req?, id}</c>, <c>dir</c> the pool it lands in (which can differ from <paramref name="callerDir"/>).</summary>
    void CreateMessage(string callerDir, JsonObject args, StateCallOptions? options = null);

    /// <summary>Replaces the body of the response to <paramref name="requestPath"/>, or with <paramref name="amend"/> of the request itself; every check runs first and nothing is written when one fails. Returns <c>{id, path, bytes, rev?}</c>.</summary>
    JsonObject FillResponse(string id, string from, string requestPath, string body, StateCallOptions? options = null, bool amend = false, bool part = false);

    /// <summary>Moves the message files <paramref name="files"/> (all in the pool's <c>msgs/</c>) into <c>msgs/archive/</c>.</summary>
    void ArchiveMessages(string dir, IReadOnlyList<string> files, StateCallOptions? options = null);

    /// <summary>Writes a team snapshot into the pool's snapshots; <paramref name="args"/> names the team and the stamp.</summary>
    JsonValue Snapshot(string dir, JsonObject args, StateCallOptions? options = null);

    /// <summary>Inserts or refreshes one team-history entry of the pool, under the team lock.</summary>
    JsonValue UpsertHistory(string dir, JsonObject entry, StateCallOptions? options = null);

    /// <summary>Appends a peer-roster row to the pool's <c>peers.jsonl</c>.</summary>
    void AppendRosterRecord(string dir, JsonValue record, StateCallOptions? options = null);

    /// <summary>
    /// Stores the tracked-job record <paramref name="record"/> as job <paramref name="id"/> of the team jobs folder <paramref name="team"/> in pool
    /// <paramref name="dir"/>; the whole record is replaced. With <paramref name="ifOpen"/>, <paramref name="record"/> holds only the fields to change (a null removes
    /// one), merged onto the stored record inside the write; a job that has ended (or is gone) is left as it is. <paramref name="merge"/> merges
    /// the same way whether or not the job has ended; a job that is gone is left alone.
    /// </summary>
    void PutJob(string dir, string team, string id, JsonObject record, bool ifOpen = false, StateCallOptions? options = null, bool merge = false);

    /// <summary>How long an ended job record is kept; <see cref="PruneJobs"/> never deletes a record that ended more recently.</summary>
    static readonly TimeSpan JobRetention = TimeSpan.FromHours(24);

    /// <summary>Deletes the job records of pool <paramref name="dir"/> that ended before <paramref name="cutoffMs"/> (epoch milliseconds), where the cutoff is clamped to at most <see cref="JobRetention"/> before now so no caller can delete a younger record; open records are never deleted.</summary>
    void PruneJobs(string dir, double cutoffMs, StateCallOptions? options = null);

    /// <summary>Appends a gate row to the pool's <c>gates.jsonl</c>.</summary>
    void AppendGate(string dir, JsonValue record, StateCallOptions? options = null);

    /// <summary>Validates a decision against the run's log at <paramref name="path"/> and appends it: <c>{refused}</c> with nothing written, or <c>{id, path, counts}</c>.</summary>
    JsonObject AppendDecision(string path, JsonValue? runId, JsonValue? input, StateCallOptions? options = null);

    /// <summary>Appends the <c>merge</c> line of a pinned merge command that ran to the run's log at <paramref name="path"/>.</summary>
    void AppendMergeRecord(string path, JsonValue merge, StateCallOptions? options = null);

    /// <summary>Appends <paramref name="row"/>, one AskUserQuestion call and its response, to <c>&lt;hier&gt;/asks.jsonl</c> of <paramref name="dir"/>; the file is created readable by its owner only.</summary>
    void AppendAsk(string dir, JsonValue row, StateCallOptions? options = null);

    /// <summary>Appends <paramref name="row"/>, one gate decision on an AskUserQuestion call, to the gate audit file of <paramref name="dir"/>, never to the user's answers.</summary>
    void AppendAskGate(string dir, JsonValue row, StateCallOptions? options = null);

    /// <summary>
    /// Appends one row to the user's question log of <paramref name="dir"/> (rows past 30 days dropped); a row whose id is held is ignored,
    /// or, with <paramref name="replace"/>, takes that row's body and time and keeps its place.
    /// </summary>
    void AppendQaLog(string dir, JsonValue row, StateCallOptions? options = null, bool replace = false);

    /// <summary>Appends <paramref name="line"/>, one line of the parked-question store (<see cref="ParkedQuestions"/>), to <paramref name="dir"/>; owner-only, and never mixed into the user's answers.</summary>
    void AppendParkedQuestion(string dir, JsonValue line, StateCallOptions? options = null);

    /// <summary>Records the user's Ultra-Advisor gate answer for a session; throws on a choice the gate does not know.</summary>
    void SetGateDecision(string sessionId, string choice, string? cwd, StateCallOptions? options = null);

    /// <summary>Forgets a session's Ultra-Advisor gate answer; true when something was removed.</summary>
    bool ClearGateDecision(string sessionId, StateCallOptions? options = null);

    /// <summary>Appends the usage row of a finished subagent to the HOME-global usage log.</summary>
    void AppendUsage(JsonValue record, StateCallOptions? options = null);

    /// <summary>
    /// One op of the <c>worktree-grants</c> domain (assign, allow, revoke, gate, audit); <paramref name="args"/> carries the
    /// hierarchy directory and team it acts on. A refused change throws with the reason as its message.
    /// </summary>
    JsonValue Grants(string op, JsonObject args, StateCallOptions? options = null);

    /// <summary>
    /// One op of the <c>progress</c> domain (create-if-absent, journal-append, state-cas, nudged-append, session-append, plan-append, checklist-rewrite) on the
    /// team's progress folder; <paramref name="args"/> carries the pool <c>dir</c> and the <c>team</c>. Returns what the op returned.
    /// </summary>
    JsonValue Progress(string op, JsonObject args, StateCallOptions? options = null);

    /// <summary>
    /// Runs one op (<c>write</c> or <c>read</c>) of the <c>checkpoint</c> domain, which keeps a team's checkpoint records;
    /// <paramref name="args"/> carries the pool <c>dir</c> and the <c>team</c>. Returns what the op returned.
    /// </summary>
    JsonValue Checkpoint(string op, JsonObject args, StateCallOptions? options = null);

    /// <summary>One op (<c>claim</c>, <c>release</c>, <c>leave</c>, <c>reap</c>, or the read <c>rows</c>) of the machine-wide <c>resources</c> domain (adr-36). Returns what the op returned.</summary>
    JsonValue Resources(string op, JsonObject args, StateCallOptions? options = null);
}

internal sealed class HierarchyStateCalls(HierLib lib, IStateWriter? writer, IRosterCore roster, IAhEnvironment environment) : IHierarchyStateCalls
{
    private static readonly StateCallOptions Hook = new(Hook: true);

    private static InvalidOperationException Failure(string message, string? code)
    {
        var failure = new InvalidOperationException(message);
        failure.Data["code"] = code;
        return failure;
    }

    private IStateWriter Writer => writer ?? throw new InvalidOperationException("no state writer is configured");

    private JsonValue Run(string domain, string op, JsonValue args, RequestContext context, StateCallOptions? options)
    {
        if (!Writer.IsHomeScoped(domain))
        {
            lib.EnsureProjectDirs(context, options?.Hook == true, domain);
        }

        var result = Writer.Invoke(domain, op, JsonText.ToUtf8(args, 0), context, options);
        if (!result.Ok)
        {
            throw Failure(result.Error ?? result.ErrorCode ?? "state call failed", result.ErrorCode);
        }

        return JsonText.Parse(result.ResultJson.Span);
    }

    public StateUpdateResult UpdateTeamFile(string path, string dir, Func<JsonValue, TeamFileOutcome?> mutate, StateCallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        JsonValue? written = null;
        var plan = new StateUpdatePlan(
            () =>
            {
                var read = roster.ReadTeamWithDigest(path);
                var record = read["doc"];
                ReadOnlyMemory<byte>? bytes = null;
                if (!record.IsNull)
                {
                    bytes = JsonText.ToUtf8(record, 0);
                }

                return new StateDocument(bytes, read["digest"].AsString());
            },
            document =>
            {
                var record = document.Bytes is { } bytes ? JsonText.Parse(bytes.Span) : JsonValue.Null;
                var outcome = mutate(record);
                written = outcome?.Record;
                if (outcome is null)
                {
                    return null;
                }

                DropStaleDuty(outcome.Record);

                var encoded = outcome.IsClear
                    ? new JsonObject { ["clear"] = true }
                    : new JsonObject { ["write"] = outcome.Record!.Value };
                return JsonText.ToUtf8(encoded, 0);
            },
            (outcome, digest) =>
            {
                var decided = JsonText.Parse(outcome.Span);
                if (decided.Member("clear") is { IsTruthy: true })
                {
                    return new StateCallSpec("clear", JsonText.ToUtf8(new JsonObject { ["path"] = path, ["dir"] = dir, ["expect"] = digest }, 0));
                }

                return new StateCallSpec(
                    "write",
                    JsonText.ToUtf8(new JsonObject { ["path"] = path, ["team"] = decided.Member("write")!.Value, ["dir"] = dir, ["expect"] = digest }, 0));
            });

        var poolContext = lib.PoolContext(dir);
        lib.EnsureProjectDirs(poolContext, options?.Hook == true);
        var result = Writer.Update("team", plan, poolContext, options);
        if (!result.Result.Ok)
        {
            throw Failure(result.Result.Error ?? result.Result.ErrorCode ?? "state update failed", result.Result.ErrorCode);
        }

        BindOrchestratorSession(dir, written);
        return result;
    }

    /// <summary>A <c>duty</c> whose deputy names no member row of the record (dismissed, untracked, reaped) is stale: the write drops it.</summary>
    internal static void DropStaleDuty(JsonValue? record)
    {
        if (record is not { IsMap: true } r || r.AsObject().Get("duty") is not { } duty)
        {
            return;
        }

        var deputy = duty.Member("deputy") is { IsText: true } d ? d.AsString() : null;
        var present = deputy is { Length: > 0 } && r.AsObject().Get("members") is { IsArray: true } members
            && members.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == deputy);
        if (!present)
        {
            r.AsObject().Remove("duty");
        }
    }

    /// <summary>
    /// The Orchestrator that writes its own team binds this session to its process, so a session that started before session records
    /// existed is still found by the status filter. A team written for another pid binds nothing.
    /// </summary>
    private void BindOrchestratorSession(string dir, JsonValue? team)
    {
        var sessionId = environment.GetVariable("CLAUDE_CODE_SESSION_ID");
        var pid = team?.Member("orchestrator")?.Member("pid");
        if (string.IsNullOrEmpty(sessionId) || pid is not { IsNumber: true } p)
        {
            return;
        }

        var value = p.AsNumber();
        if (value != Math.Floor(value) || !double.TryParse(environment.GetVariable("CLAUDE_PID"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var own) || value != own)
        {
            return;
        }

        lib.AppendSessionPid(dir, sessionId, p);
    }

    public StateUpdateResult UpdateTeam(string dir, string? team, Func<JsonValue, TeamFileOutcome?> mutate, StateCallOptions? options = null) =>
        UpdateTeamFile(roster.TeamPath(dir, team), dir, mutate, options);

    public void AppendPeerRecord(JsonValue record, StateCallOptions? options = null) =>
        Run("peer-records", "append", new JsonObject { ["rec"] = record }, lib.CwdContext(), options ?? Hook);

    public void CreateMessage(string callerDir, JsonObject args, StateCallOptions? options = null) =>
        Run("msgs", "create", args, lib.PoolContext(callerDir), options);

    public JsonObject FillResponse(string id, string from, string requestPath, string body, StateCallOptions? options = null, bool amend = false, bool part = false)
    {
        var plan = lib.FillPlan(new JsonObject { ["id"] = id, ["from"] = from, ["reqPath"] = requestPath, ["body"] = body, ["amend"] = amend, ["part"] = part });
        var path = plan.Member("path")!.Value.AsString();
        var dir = NodePath.Dirname(NodePath.Dirname(path));
        var args = new JsonObject { ["dir"] = dir, ["path"] = path, ["req"] = requestPath, ["id"] = id, ["from"] = from, ["body"] = body };
        if (amend)
        {
            args["amend"] = true;
        }

        if (part)
        {
            args["part"] = true;
        }

        var result = Run("msgs", "fill", args, lib.PoolContext(dir), options);
        var filled = new JsonObject { ["id"] = id, ["path"] = path };
        if (result.Member("bytes") is { } bytes)
        {
            filled["bytes"] = bytes;
        }

        if (result.Member("rev") is { } rev)
        {
            filled["rev"] = rev;
        }

        return filled;
    }

    public void ArchiveMessages(string dir, IReadOnlyList<string> files, StateCallOptions? options = null)
    {
        var names = new JsonArray();
        foreach (var file in files)
        {
            names.Add(file);
        }

        Run("msgs", "archive", new JsonObject { ["dir"] = dir, ["files"] = names }, lib.PoolContext(dir), options);
    }

    public JsonValue Snapshot(string dir, JsonObject args, StateCallOptions? options = null) =>
        Run("team", "snapshot", Merged(dir, args), lib.PoolContext(dir), options);

    public JsonValue UpsertHistory(string dir, JsonObject entry, StateCallOptions? options = null) =>
        Run("team", "history", Merged(dir, entry), lib.PoolContext(dir), options);

    private static JsonObject Merged(string dir, JsonObject fields)
    {
        var merged = new JsonObject { ["dir"] = dir };
        foreach (var key in fields.Keys)
        {
            merged[key] = fields[key];
        }

        return merged;
    }

    public void AppendRosterRecord(string dir, JsonValue record, StateCallOptions? options = null) =>
        Run("peers", "append", new JsonObject { ["dir"] = dir, ["rec"] = record }, lib.PoolContext(dir), options);

    public void PutJob(string dir, string team, string id, JsonObject record, bool ifOpen = false, StateCallOptions? options = null, bool merge = false) =>
        Run("jobs", "put", new JsonObject { ["dir"] = dir, ["team"] = team, ["id"] = id, ["record"] = JsonValue.FromObject(record), ["if_open"] = ifOpen, ["merge"] = merge }, lib.PoolContext(dir), options ?? Hook);

    public void PruneJobs(string dir, double cutoffMs, StateCallOptions? options = null) =>
        Run("jobs", "prune", new JsonObject { ["dir"] = dir, ["cutoff_ms"] = cutoffMs }, lib.PoolContext(dir), options ?? Hook);

    public void AppendGate(string dir, JsonValue record, StateCallOptions? options = null) =>
        Run("gates", "append", new JsonObject { ["dir"] = dir, ["rec"] = record }, lib.PoolContext(dir), options ?? Hook);

    public JsonObject AppendDecision(string path, JsonValue? runId, JsonValue? input, StateCallOptions? options = null)
    {
        var args = new JsonObject { ["path"] = path };
        if (runId is { } run)
        {
            args["run"] = run;
        }

        if (input is { } value)
        {
            args["input"] = value;
        }

        var result = Run("decisions", "add", args, lib.PoolContext(PoolOfLog(path)), options);
        if (result.Member("refused") is { IsTruthy: true })
        {
            return result.AsObject();
        }

        return new JsonObject { ["id"] = result.Member("id") ?? JsonValue.Null, ["path"] = path, ["counts"] = result.Member("counts") ?? JsonValue.Null };
    }

    public void AppendMergeRecord(string path, JsonValue merge, StateCallOptions? options = null) =>
        Run("decisions", "append", new JsonObject { ["path"] = path, ["merge"] = merge }, lib.PoolContext(PoolOfLog(path)), options ?? Hook);

    public void AppendAsk(string dir, JsonValue row, StateCallOptions? options = null) =>
        Run("decisions", "ask", new JsonObject { ["path"] = NodePath.Join(dir, "asks.jsonl"), ["row"] = row }, lib.PoolContext(dir), options);

    public void AppendQaLog(string dir, JsonValue row, StateCallOptions? options = null, bool replace = false)
    {
        var args = new JsonObject { ["dir"] = dir, ["row"] = row };
        if (replace)
        {
            args["replace"] = true;
        }

        Run("qa-log", "append", args, lib.PoolContext(dir), options ?? Hook);
    }

    public void AppendAskGate(string dir, JsonValue row, StateCallOptions? options = null) =>
        Run("decisions", "ask", new JsonObject { ["path"] = NodePath.Join(dir, HierLib.AsksGateFile), ["row"] = row }, lib.PoolContext(dir), options ?? Hook);

    public void AppendParkedQuestion(string dir, JsonValue line, StateCallOptions? options = null) =>
        Run("decisions", "ask", new JsonObject { ["path"] = ParkedQuestions.PathIn(dir), ["row"] = line }, lib.PoolContext(dir), options ?? Hook);

    private static string PoolOfLog(string path) => NodePath.Dirname(NodePath.Dirname(NodePath.Dirname(path)));

    public void SetGateDecision(string sessionId, string choice, string? cwd, StateCallOptions? options = null)
    {
        var args = new JsonObject { ["session_id"] = sessionId, ["choice"] = choice };
        if (cwd is not null)
        {
            args["cwd"] = cwd;
        }

        Run("ultra-gate", "write", args, lib.CwdContext(), options);
    }

    public bool ClearGateDecision(string sessionId, StateCallOptions? options = null) =>
        Run("ultra-gate", "write", new JsonObject { ["session_id"] = sessionId, ["clear"] = true }, lib.CwdContext(), options)
            .Member("removed") is { IsBool: true } removed && removed.AsBool();

    public JsonValue Grants(string op, JsonObject args, StateCallOptions? options = null) =>
        Run("worktree-grants", op, args, args.Get("dir") is { IsText: true } dir && dir.AsString().Length > 0 ? lib.PoolContext(dir.AsString()) : lib.CwdContext(), options);

    public JsonValue Progress(string op, JsonObject args, StateCallOptions? options = null) =>
        Run("progress", op, args, lib.PoolContext(args.Get("dir") is { IsText: true } dir ? dir.AsString() : throw new ArgumentException("dir is required")), options);

    public JsonValue Checkpoint(string op, JsonObject args, StateCallOptions? options = null) =>
        Run("checkpoint", op, args, lib.PoolContext(args.Get("dir") is { IsText: true } dir ? dir.AsString() : throw new ArgumentException("dir is required")), options);

    public JsonValue Resources(string op, JsonObject args, StateCallOptions? options = null) =>
        Run("resources", op, args, lib.CwdContext(), options ?? new StateCallOptions());

    public void AppendUsage(JsonValue record, StateCallOptions? options = null) =>
        Run("usage", "append", new JsonObject { ["rec"] = record }, lib.CwdContext(), options ?? Hook);
}
