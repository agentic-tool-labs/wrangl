using System.Globalization;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>What an allowed decision write records of its caller.</summary>
    private sealed record DecisionStamp(string AnsweredBy, string RecordedBy, string Caller, string? SurrogateClass);

    /// <summary>
    /// Proves who is running a decision verb before the decision log is read, from this process's own ancestry. The audit row is
    /// appended before the outcome is acted on; a refusal, or an audit that cannot be written, writes nothing and gives exit 3.
    /// </summary>
    private DecisionStamp? ProveDecisionCaller(DecisionAction action, string dir, string runId, string? id, string? choice, string logPath, out int exit)
    {
        exit = 0;
        var canonical = PlatformModule.CanonicalAhHome(_env, _graph.Io.Files);
        if (!canonical.Accepted)
        {
            exit = RefuseDecisionProof(action, id, DecisionProof.HomeUnresolved, canonical.Reason, _teamArg);
            return null;
        }

        var envHome = PlatformModule.ResolveHomePath(_graph.Io.Files, _env.AhHome) ?? _env.AhHome;
        if (envHome != canonical.Home)
        {
            var mismatch = AuditDecision(canonical.Home, action, runId, _teamArg, id, choice, "unclassified", NoCaller, DecisionProof.HomeMismatch, _env.AhHome);
            exit = RefuseDecisionProof(action, id, mismatch is null ? DecisionProof.HomeMismatch : DecisionProof.AuditUnwritable, mismatch, _teamArg);
            return null;
        }

        var who = ResolveDecisionCaller();
        var team = _teamArg;
        var memberOfAnchorTeam = team is not null && who.OrchestratorPids.Count > 0 && IsMemberOfTeam(who.OrchestratorPids, team);
        var teamDirIsThisDir = who.TeamHierDir is { Length: > 0 } && Path.GetFullPath(who.TeamHierDir) == Path.GetFullPath(HierarchyDir);
        var verdict = DecisionProof.Judge(who.Class, team, memberOfAnchorTeam, teamDirIsThisDir);
        var code = verdict.Code;
        string? surrogateClass = null;
        if (code is null && verdict.Caller is "top-orchestrator" or "deputy-chief" && action == DecisionAction.Answer)
        {
            if (id is not null && ParkedIsDangerous(logPath, id))
            {
                code = DecisionProof.DangerousNeedsUser;
            }
            else if (_opts.Text("surrogate") is { } asked)
            {
                if (!DecisionProof.IsDelegableClass(asked))
                {
                    code = "surrogate-not-allowed: class";
                }
                else if (!SurrogateConfirmed(dir, who.SessionId))
                {
                    code = "surrogate-not-allowed: authority";
                }
                else
                {
                    surrogateClass = asked;
                }
            }

            if (code is null && surrogateClass is null && verdict.Caller == "deputy-chief")
            {
                code = DecisionProof.DeputyNeedsSurrogate;
            }
        }

        var failure = AuditDecision(canonical.Home, action, runId, team, id, choice, verdict.Caller, who, code, null);
        if (failure is not null)
        {
            exit = RefuseDecisionProof(action, id, DecisionProof.AuditUnwritable, failure, team);
            return null;
        }

        if (code is not null)
        {
            exit = RefuseDecisionProof(action, id, code, null, team);
            return null;
        }

        return new DecisionStamp(surrogateClass is null ? "user" : "surrogate", verdict.RecordedBy, verdict.Caller, surrogateClass);
    }

    private DecisionCaller ResolveDecisionCaller()
    {
        if (_decisionCaller?.Invoke() is { } supplied)
        {
            return supplied;
        }

        var table = PlatformModule.CreateProcessTable(_graph.Runner);
        var principal = Principal;
        var facts = new DaemonCutoverFacts(_graph, table, ProofHierarchyDirs);
        var classified = CutoverCallers.Classify(principal, facts, _teamArg);
        var orchestrating = principal.Sessions.Where(s => s.Addresses.Any(a => a.StartsWith("orchestrator@", StringComparison.Ordinal))).ToList();
        var team = DecisionProof.TeamOf(classified.Detail);
        var address = HierarchyModule.OrchestratorAddress(team);
        var session = orchestrating.FirstOrDefault(s => s.Addresses.Contains(address))?.SessionId;
        var teamDir = team is not null && session is not null && facts.TeamFileEntry(session, address) is { Length: > 0 } entry ? CutoverCallers.HierDirOf(entry, team) : null;
        var pid = Environment.ProcessId;
        return new DecisionCaller(classified, [.. orchestrating.Select(s => s.Pid)], session, pid, table.StartTime(pid), table.Tty(pid) is { Length: > 0 } tty ? tty : null, teamDir);
    }

    /// <summary>The hierarchy directories the member-row test reads: this one, the index of known ones, and those the live registrations name.</summary>
    private IReadOnlyList<string> ProofHierarchyDirs()
    {
        var dirs = new List<string> { HierarchyDir };
        dirs.AddRange(Hier.Status.KnownHierarchyDirs());
        foreach (var registration in _graph.Mailbox.LiveRegistrations())
        {
            if (registration.Get("team_files") is { IsMap: true } files)
            {
                foreach (var key in files.AsObject().Keys)
                {
                    if (files.AsObject().Get(key) is { IsText: true } path && Path.GetDirectoryName(Path.GetDirectoryName(path.AsString())) is { Length: > 0 } dir)
                    {
                        dirs.Add(dir);
                    }
                }
            }
        }

        return [.. dirs.Distinct(StringComparer.Ordinal)];
    }

    private bool IsMemberOfTeam(IReadOnlyList<int> pids, string team) =>
        RosterCore.ReadTeamRecord(HierarchyDir, team) is { IsMap: true } record
        && record.AsObject().Get("members") is { IsArray: true } members
        && members.AsArray().Any(m => m.IsMap && pids.Any(p => DaemonCutoverFacts.NamesPid(m.AsObject(), p)));

    /// <summary>Whether any parked line with <paramref name="id"/> is dangerous; a log that cannot be read whole counts as dangerous.</summary>
    private bool ParkedIsDangerous(string logPath, string id)
    {
        try
        {
            var read = Hier.Decisions.ReadDecisions(logPath);
            if (read["skipped"] is { IsNumber: true } skipped && skipped.AsNumber() > 0)
            {
                return true;
            }

            return read["decisions"].AsArray().Any(line =>
                line.Member("kind") is { IsText: true } kind && kind.AsString() == "parked"
                && line.Member("id") is { IsText: true } have && have.AsString() == id
                && !(line.Member("dangerous") is { IsBool: true } dangerous && !dangerous.AsBool()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Whether the run anchor's grant is <c>surrogate</c> and the Orchestrator's recorded Authority answer confirms it, by the resolver <c>whoami</c> uses.</summary>
    private bool SurrogateConfirmed(string dir, string? session) =>
        HierarchyModule.ReadAuthority(Hier, _graph.Io.Files, dir, _teamArg, session).Mode == "surrogate";

    /// <summary>The caller of an attempt refused before its caller was classified.</summary>
    private static readonly DecisionCaller NoCaller = new(new CutoverCaller(CutoverCallerKind.Unknown, "not classified: the environment's home is not the canonical home"), [], null, Environment.ProcessId, null, null);

    /// <summary>Appends the attempt's row to <c>decision-audit.jsonl</c> under <paramref name="home"/>, the canonical home; <paramref name="envHome"/> is the home the environment named, when it differed.</summary>
    private string? AuditDecision(string home, DecisionAction action, string runId, string? team, string? id, string? choice, string caller, DecisionCaller who, string? code, string? envHome)
    {
        var detail = new JsonObject
        {
            ["pid"] = (double)who.Pid,
            ["pid_start"] = who.PidStart is null ? JsonValue.Null : who.PidStart,
            ["nearest_session"] = who.SessionId is null ? JsonValue.Null : who.SessionId,
            ["tty"] = who.Tty is null ? JsonValue.Null : who.Tty,
        };
        var row = new JsonObject
        {
            ["at"] = _graph.Io.Clock.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["action"] = action == DecisionAction.Add ? "add" : "answer",
            ["run"] = runId,
            ["team"] = team is null ? JsonValue.Null : team,
            ["decision"] = id is null ? JsonValue.Null : id,
            ["choice"] = choice is null ? JsonValue.Null : choice,
            ["caller"] = caller,
            ["caller_detail"] = detail,
            ["proof"] = who.Class.Detail,
            ["outcome"] = code is null ? "allowed" : "refused: " + code,
        };
        if (envHome is not null)
        {
            row["env_home"] = envHome;
        }

        return AuditLog.Append(DecisionProof.AuditPath(home), row);
    }

    /// <summary>Refuses a decision write the caller may not make: the reason on standard output as JSON, the sentence on standard error, exit 3.</summary>
    private int RefuseDecisionProof(DecisionAction action, string? id, string code, string? detail, string? team)
    {
        var refused = new JsonObject { ["refused"] = true, ["reason"] = code };
        string text;
        if (code == DecisionProof.DangerousNeedsUser && id is not null)
        {
            var command = $"ah msg decision answer {id} --choice <letter> --team {team ?? "<team>"} --cwd {Where}";
            refused["command"] = command;
            text = $"ah: decision {id} is dangerous, so only the user may answer it ({code}). Run this in your own terminal: {command}";
        }
        else if (action == DecisionAction.Answer)
        {
            text = $"ah: only the user, or the run's Orchestrator recording the user's answer, may answer decision {id} ({code})";
        }
        else
        {
            text = $"ah: only the user, or the run's Orchestrator, may add a decision ({code})";
        }

        if (detail is not null)
        {
            text += $": {detail}";
        }

        refused["detail"] = text;
        _out.Json(refused);
        _out.Note(text);
        return 3;
    }
}
