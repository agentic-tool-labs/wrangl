using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

internal static class AdoptVerb
{
    private static readonly HashSet<string> Flags =
        new(StringComparer.Ordinal) { "orchestrator-pid", "team", "cwd", "force" };

    /// <summary>
    /// A lane's child team is taken over by the lane's own sub-orchestrator, under the rule the session start applies: the caller is
    /// the registered session of the member the manager's stream names, and the team is rewritten only when its owner is gone.
    /// </summary>
    private static int AdoptLaneChild(RosterContext c, string dir, string manager, string stream, double supplied)
    {
        var child = $"{manager}-{stream}";
        var refusal = $"lane-child-adopt-by-so: only the lane's sub-orchestrator may adopt {child}; respawn it or /clear its session";
        var lane = Ah.Roster.RosterModule.LaneLeaderOf(c.Services.Roster, dir, child, name => c.Services.Roster.ReadTeamRecord(dir, name), role => c.Services.Config.RoleClass(role, c.Registry) == "orchestrate");
        if (c.ProgressTeam != child || lane is not { } leader || leader.Manager != manager || c.Services.OwnerProof is not { } proof)
        {
            throw new RosterFailure(refusal);
        }

        var nearest = proof.NearestAncestor(dir, [dir]);
        var tied = OrchestratorSession.ProcessTied(c, supplied);
        // A subagent is stopped before this point by the owner-verb gate, so a caller here is the lane's own session.
        // The owner guard is the primary refusal of a pid that is not the caller's; the pid match here is defence in depth.
        var registration = nearest.Pid == (int)supplied
            ? proof.RegistrationsOf(nearest.Pid).FirstOrDefault(r => r.Address == $"{leader.Member}@{manager}" && !string.IsNullOrEmpty(r.SessionId) && (tied is null || tied == r.SessionId))
            : null;
        if (registration is null)
        {
            throw new RosterFailure(refusal);
        }

        var result = Ah.Hierarchy.HierarchyModule.AdoptLaneChild(
            c.Services.Hierarchy.StateCalls,
            c.Services.Roster,
            dir,
            manager,
            stream,
            child,
            new Ah.Hierarchy.LaneChildOwner(registration.SessionId, (int)supplied, OrchestratorSession.StartOf(c, supplied)));
        if (result.Refusal is { } why)
        {
            throw new RosterFailure(why);
        }

        var output = new JsonObject { ["adopted"] = true, ["team"] = child };
        if (result.AdoptedFrom is { } from)
        {
            output["adopted_from"] = from;
        }

        c.Out(output);
        return 0;
    }

    /// <summary>
    /// Recovery for a team whose owner pid is null or dead: re-stamps <c>orchestrator.pid</c> without touching members or the
    /// team id. It refuses whenever the recorded owner is alive and different.
    /// </summary>
    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"adopt: unrecognized flag --{key} (use --orchestrator-pid, --team, --cwd, or --force)");
            }
        }

        var supplied = double.NaN;
        if (c.Opts.Text("orchestrator-pid") is { } raw)
        {
            supplied = JsConvert.ToNumber(JsonValue.FromString(raw));
        }

        if (!RosterContext.IsInteger(supplied))
        {
            throw new RosterFailure("adopt needs --orchestrator-pid <pid>: the pid of the live Claude session taking the team over, which must be this command's own session");
        }

        var suppliedText = JsConvert.NumberToString(supplied);
        if (!c.PidAlive(supplied))
        {
            throw new RosterFailure($"adopt: --orchestrator-pid {suppliedText} is not a live process");
        }

        OrchestratorSession.Rebind(c);
        var dir = c.Dir;
        var target = c.Services.Roster.TeamFileState(dir, c.TeamFile);
        if (target.Get("state") is { IsText: true } state && state.AsString() == "unusable")
        {
            var path = target.Get("path") is { } p ? JsConvert.ToJsString(p) : "undefined";
            throw new RosterFailure(
                $"adopt: the team file {path} exists but does not read as a team record, so there is nothing to adopt and it was not rewritten. Repair or remove that file");
        }

        if (target.Get("team") is not { IsTruthy: true })
        {
            throw new RosterFailure("adopt: no team file at this scope to adopt");
        }

        if (target.Get("team") is { } record && record.Member("parent") is { IsMap: true } link
            && link.Member("team") is { IsText: true } managerText && link.Member("stream") is { IsText: true } streamText)
        {
            return AdoptLaneChild(c, dir, managerText.AsString(), streamText.AsString(), supplied);
        }

        JsonValue adopted = JsonValue.Null;
        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                if (!doc.IsTruthy)
                {
                    throw new RosterFailure("adopt: no team file at this scope to adopt");
                }

                var orchestrator = doc.Member("orchestrator");
                var heldPid = RosterJson.Member(orchestrator, "pid");
                var selfPid = heldPid is { IsNumber: true } hp && hp.AsNumber() == supplied;
                var selfSession = RosterJson.Member(orchestrator, "session_id") is { IsText: true, } recordedSession && recordedSession.AsString().Length > 0
                    && OrchestratorSession.ProcessTied(c, supplied) == recordedSession.AsString();
                if (OrchestratorSession.IsClaimed(orchestrator) && !selfPid && !selfSession)
                {
                    var (life, why) = c.Services.Roster.TeamLifeWhy(doc);
                    var named = heldPid is { IsNull: false } held ? "pid " + JsConvert.ToJsString(held) : "the recorded session";
                    if (life == Ah.Roster.TeamLife.OwnedLive)
                    {
                        throw new RosterFailure($"adopt: team is owned by live {named} ({why}) — refusing to hijack a live team, and --force does not override a proven-live owner");
                    }

                    if (life == Ah.Roster.TeamLife.Indeterminate && !c.Opts.IsTrue("force"))
                    {
                        throw new RosterFailure($"adopt: whether {named} still owns the team cannot be proven gone ({why}) — refusing; adopt --force takes the team over anyway");
                    }
                }

                var copy = doc.IsMap ? doc.AsObject().Clone() : new JsonObject();
                var owner = orchestrator is { IsMap: true } o ? o.AsObject().Clone() : new JsonObject();
                owner["pid"] = supplied;
                owner.Remove("released");
                if (OrchestratorSession.StartOf(c, supplied) is { } started)
                {
                    owner["pid_start"] = started;
                }
                else
                {
                    owner.Remove("pid_start");
                }

                if (OrchestratorSession.ProcessTied(c, supplied) is { } tied)
                {
                    owner["session_id"] = tied;
                }
                else
                {
                    owner["session_id"] = JsonValue.Null;
                    c.OrchestratorSessionUnknown = c.PidAlive(supplied);
                }

                copy["orchestrator"] = owner;
                adopted = copy;
                return TeamFileOutcome.Write(copy);
            });

        var result = new JsonObject { ["adopted"] = true };
        if (adopted.Member("team_id") is { } id)
        {
            result["team_id"] = id;
        }

        result["orchestrator"] = adopted.Member("orchestrator") ?? JsonValue.Null;
        OrchestratorSession.Warn(c, result);
        c.Out(result);
        return 0;
    }
}
